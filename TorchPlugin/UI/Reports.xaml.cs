using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Utils;
using Advanced_PB_Limiter.Utils.Reports;
using ExtensionMethods;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using MessageBox = System.Windows.MessageBox;
using static System.FormattableString;
// ReSharper disable SpecifyACultureInStringConversionExplicitly
// ReSharper disable TooWideLocalVariableScope

namespace Advanced_PB_Limiter.UI
{
    public partial class Reports
    {
        // The grids are bound once. Rows are created for new blocks and players, updated in place while they are tracked,
        // and detached when they leave, so WPF keeps every row container instead of rebuilding both grids on each tick.
        private readonly ObservableCollection<CustomBlockReport> _liveBlockRows = new();
        private readonly ObservableCollection<CustomPlayerReport> _livePlayerRows = new();
        private readonly Dictionary<long, CustomBlockReport> _allBlocks = new();
        private readonly Dictionary<long, CustomPlayerReport> _allPlayers = new();
        private readonly HashSet<long> _visibleBlockIds = new();
        private readonly HashSet<long> _seenBlockIds = new();
        private readonly HashSet<long> _seenPlayerIds = new();
        private readonly List<TrackedPlayer> _playerScratch = new();
        private readonly List<(TrackedPBBlock Block, bool Resting)> _blockScratch = new();
        private readonly List<long> _staleIds = new();
        private readonly Action<TrackedPBBlock, bool> _addBlock;
        private readonly System.Text.StringBuilder _updatedText = new();
        // The player whose blocks PlayerTotals.Of is adding, during a refresh only, so no player is kept after it.
        private TrackedPlayer? _currentPlayer;

        // A DispatcherTimer coalesces ticks while the UI is busy, where a System.Timers.Timer posted a refresh for each
        // tick and a busy window let them pile up.
        private readonly DispatcherTimer _refreshTimer;
        // The admin asked for the live view. It stays on while the tab is hidden, but the timer only runs while it is
        // visible, so a hidden view costs nothing and resumes when the tab is opened again.
        private bool _liveViewEnabled;
        private bool _initialized;
        private int _restingCount;
        private DateTime _lastUpdatedUtc;
        private string? _lastRefreshError;
        private DateTime _lastRefreshErrorAtUtc;
        private const string fileTypes = "Text File (*.txt)|*.txt";
        private const int ReportWidth = 80;
        private const int StatLabelWidth = 27;
        private const double MinRefreshSeconds = 0.1;
        private const double MaxRefreshSeconds = 3600;
        private static readonly TorchLog Log = new("Advanced_PB_Limiter.UI.Reports");

        public Reports()
        {
            InitializeComponent();
            DataContext = this;
            _addBlock = AddBlock;

            PerPBDataGrid!.ItemsSource = _liveBlockRows;
            PerPlayerDataGrid!.ItemsSource = _livePlayerRows;
            DisableLiveViewButton!.IsEnabled = false;

            // On the control's own dispatcher, at Background priority: a refresh waits for the UI to be free instead of
            // queueing another one.
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _refreshTimer.Tick += TimedRefresh;
            Loaded += (_, _) => UpdateLiveViewTimer(refreshNow: true);
            Unloaded += (_, _) => PauseLiveView();
            IsVisibleChanged += (_, _) => UpdateLiveViewTimer(refreshNow: true);

            _initialized = true;
            UpdateSummary();
        }

        /// <summary>Called from the game thread when the world unloads. Stops the live view and drops the stopped world's rows.</summary>
        internal void OnSessionUnloading()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            try
            {
                _ = Dispatcher.BeginInvoke(new Action(ClearAfterUnload));
            }
            catch (InvalidOperationException)
            {
                // The dispatcher began shutting down between the check and the call.
            }
        }

        private void ClearAfterUnload()
        {
            ClearRows();
        }

        private void ClearRows()
        {
            StopLiveView();
            _currentPlayer = null;

            foreach (CustomBlockReport row in _allBlocks.Values)
                row.Detach();
            foreach (CustomPlayerReport row in _allPlayers.Values)
                row.Detach();

            _allBlocks.Clear();
            _allPlayers.Clear();
            _liveBlockRows.Clear();
            _livePlayerRows.Clear();
            _visibleBlockIds.Clear();
            _seenBlockIds.Clear();
            _seenPlayerIds.Clear();
            _blockScratch.Clear();
            _playerScratch.Clear();
            _staleIds.Clear();
            _restingCount = 0;
            _lastUpdatedUtc = default;

            NoticeBorder.Visibility = Visibility.Collapsed;
            NoticeText.Text = string.Empty;
            UpdatedText.Text = "No report read yet.";
            UpdateSummary();
        }

        private void TimedRefresh(object? sender, EventArgs e)
        {
            RefreshReportData();
        }

        /// <summary>
        /// Fills and updates the grids. It reads only tracking data, and the block and grid names and state the game
        /// thread cached in each block (see TrackedPBBlock.RefreshLiveState), so the live view does no per-refresh work
        /// on the game thread.
        /// </summary>
        private void RefreshReportData()
        {
            try
            {
                RefreshReportRows();
            }
            catch (Exception e)
            {
                NoticeText.Text = "Could not read the report: " + e.Message;
                NoticeBorder.Visibility = Visibility.Visible;

                // A refresh that fails on every tick must not fill Torch's log. Report each reason once, and again only
                // after five minutes while it persists.
                if (!string.Equals(_lastRefreshError, e.Message, StringComparison.Ordinal)
                    || DateTime.UtcNow - _lastRefreshErrorAtUtc > TimeSpan.FromMinutes(5))
                {
                    _lastRefreshError = e.Message;
                    _lastRefreshErrorAtUtc = DateTime.UtcNow;
                    Log.Error(e, "Could not read the PB Limiter report");
                }
            }
        }

        /// <summary>Fills the grids before the caller reads them, for the generated report; a failure is the caller's to report.</summary>
        private void RefreshReportDataNow()
        {
            RefreshReportRows();
        }

        private void RefreshReportRows()
        {
            _seenPlayerIds.Clear();
            _seenBlockIds.Clear();
            _restingCount = 0;

            TrackingManager.CopyTrackedPlayers(_playerScratch);

            for (int index = 0; index < _playerScratch.Count; index++)
            {
                TrackedPlayer player = _playerScratch[index];
                _seenPlayerIds.Add(player.PlayerId);

                _currentPlayer = player;
                PlayerTotals totals = PlayerTotals.Of(player, _blockScratch, _addBlock);

                if (!_allPlayers.TryGetValue(player.PlayerId, out CustomPlayerReport? playerRow))
                {
                    playerRow = new CustomPlayerReport(player, totals);
                    _allPlayers.Add(player.PlayerId, playerRow);
                    _livePlayerRows.Add(playerRow);
                }
                else
                {
                    playerRow.Update(player, totals);
                }
            }
            // Nothing of the world is kept between refreshes: the rows hold what they show.
            _currentPlayer = null;
            _playerScratch.Clear();
            _blockScratch.Clear();

            RemoveUnseenRows();
            ApplyLiveVisibility();
            UpdateSummary();

            _lastUpdatedUtc = DateTime.UtcNow;
            _updatedText.Clear();
            _updatedText.Append("Updated ")
                .Append(_lastUpdatedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(" · ")
                .Append(_allBlocks.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" blocks · ")
                .Append(_allPlayers.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" players");
            UpdatedText.Text = _updatedText.ToString();
            NoticeBorder.Visibility = Visibility.Collapsed;
            NoticeText.Text = string.Empty;
        }

        private void AddBlock(TrackedPBBlock block, bool resting)
        {
            if (_currentPlayer is not { } player) return;
            if (!_seenBlockIds.Add(block.EntityId)) return;
            if (resting) _restingCount++;

            if (!_allBlocks.TryGetValue(block.EntityId, out CustomBlockReport? row))
            {
                row = new CustomBlockReport(player, block, resting);
                _allBlocks.Add(block.EntityId, row);
            }
            else
            {
                row.Update(player, block, resting);
            }
        }

        private void RemoveUnseenRows()
        {
            _staleIds.Clear();
            foreach (long entityId in _allBlocks.Keys)
            {
                if (!_seenBlockIds.Contains(entityId))
                    _staleIds.Add(entityId);
            }

            foreach (long entityId in _staleIds)
            {
                CustomBlockReport row = _allBlocks[entityId];
                _allBlocks.Remove(entityId);
                if (_visibleBlockIds.Remove(entityId))
                    _liveBlockRows.Remove(row);
                row.Detach();
            }

            _staleIds.Clear();
            foreach (long playerId in _allPlayers.Keys)
            {
                if (!_seenPlayerIds.Contains(playerId))
                    _staleIds.Add(playerId);
            }

            foreach (long playerId in _staleIds)
            {
                CustomPlayerReport row = _allPlayers[playerId];
                _allPlayers.Remove(playerId);
                _livePlayerRows.Remove(row);
                row.Detach();
            }
        }

        private void ApplyLiveVisibility()
        {
            bool showResting = ShowRestingCheckBox.IsChecked == true;

            foreach (CustomBlockReport row in _allBlocks.Values)
            {
                bool visible = showResting || !row.Resting;

                if (visible)
                {
                    if (_visibleBlockIds.Add(row.EntityId))
                        _liveBlockRows.Add(row);
                }
                else if (_visibleBlockIds.Remove(row.EntityId))
                {
                    _liveBlockRows.Remove(row);
                }
            }
        }

        private void UpdateSummary()
        {
            int totalBlocks = _allBlocks.Count;
            int restingBlocks = _restingCount;
            int activeBlocks = Math.Max(0, totalBlocks - restingBlocks);
            int totalPlayers = _allPlayers.Count;

            double combinedLast = 0;
            double combinedAverage = 0;
            int offences = 0;
            int recompiles = 0;
            long memoryBytes = 0;
            bool memoryUnmeasured = false;

            foreach (CustomPlayerReport player in _allPlayers.Values)
            {
                combinedLast += player.Combined.LastRunTimeMS;
                combinedAverage += player.Combined.RunTimeMSAvg;
                offences += player.Offences;
                recompiles += player.Recompiles;
                memoryBytes += player.MemoryBytes;
                memoryUnmeasured |= player.MemoryUnmeasured;
            }

            SetText(TotalBlocksText, totalBlocks.ToString(CultureInfo.InvariantCulture));
            SetText(ActiveBlocksText, activeBlocks.ToString(CultureInfo.InvariantCulture));
            SetText(RestingBlocksText, restingBlocks.ToString(CultureInfo.InvariantCulture));
            SetText(PlayersText, totalPlayers.ToString(CultureInfo.InvariantCulture));
            SetText(CombinedLastText, combinedLast.ToString("0.0000", CultureInfo.InvariantCulture) + " ms");
            SetText(CombinedAvgText, combinedAverage.ToString("0.0000", CultureInfo.InvariantCulture) + " ms");
            SetText(OffencesText, offences.ToString(CultureInfo.InvariantCulture));
            SetText(RecompilesText, recompiles.ToString(CultureInfo.InvariantCulture));
            SetText(MemoryValueText, MemoryText.Total(memoryBytes, memoryUnmeasured));
        }

        private static void SetText(TextBlock block, string text)
        {
            if (!string.Equals(block.Text, text, StringComparison.Ordinal))
                block.Text = text;
        }

        private void EnableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (ShowRetired()) return;
            if (!LimiterCore.GameOnline)
            {
                MessageBox.Show("Game is not online, cannot enable live view.", "Game Offline", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // A NaN fails both comparisons. Outside this range the timer throws or floods the UI thread.
            // Read as the limits are, a dot for decimals: "1,000" could mean a thousand or one, so a comma is refused.
            if (Limits.TryParse(RefreshRateTextBox.Text, out double refreshRate)
                && refreshRate >= MinRefreshSeconds && refreshRate <= MaxRefreshSeconds)
            {
                RefreshRateTextBox.ClearValue(Control.BorderBrushProperty);
                RefreshRateTextBox.ClearValue(Control.ForegroundProperty);

                _refreshTimer.Interval = TimeSpan.FromSeconds(refreshRate);
                _liveViewEnabled = true;
                EnableLiveViewButton.IsEnabled = false;
                DisableLiveViewButton.IsEnabled = true;
                UpdateLiveViewTimer(refreshNow: true);
                return;
            }

            RefreshRateTextBox.BorderBrush = Brushes.Red;
            RefreshRateTextBox.Foreground = Brushes.Red;
            RefreshRateTextBox.Focus();
        }

        private void DisableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            StopLiveView();
        }

        /// <summary>Stops and forgets the live view: a later tab switch must not start it again.</summary>
        private void StopLiveView()
        {
            _liveViewEnabled = false;
            _refreshTimer.Stop();
            LiveStatusText.Text = "Stopped";
            EnableLiveViewButton.IsEnabled = true;
            DisableLiveViewButton.IsEnabled = false;
        }

        /// <summary>
        /// Runs the live view only while the tab is visible. Called when it is enabled and on load, unload and
        /// visibility changes, so a hidden tab pauses and the next visit resumes and refreshes at once.
        /// </summary>
        private void UpdateLiveViewTimer(bool refreshNow = false)
        {
            if (!_liveViewEnabled)
            {
                _refreshTimer.Stop();
                return;
            }

            if (!IsLoaded || !IsVisible)
            {
                PauseLiveView();
                return;
            }

            bool wasRunning = _refreshTimer.IsEnabled;
            if (!wasRunning)
                _refreshTimer.Start();

            LiveStatusText.Text = "Live";

            if (refreshNow && !wasRunning)
                RefreshReportData();
        }

        /// <summary>Stops the timer but keeps the live view wanted, so returning to the tab resumes it.</summary>
        private void PauseLiveView()
        {
            _refreshTimer.Stop();
            if (!_liveViewEnabled) return;
            LiveStatusText.Text = "Paused";
        }

        private void ShowResting_OnChanged(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;
            ApplyLiveVisibility();
        }

        private void ManualUpdateButton_OnClick(object sender, RoutedEventArgs e)
        {
            RefreshReportData();
        }

        private void GenerateTextReport_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshReportDataNow();
            }
            catch (Exception err)
            {
                ReportFailed(err);
                return;
            }

            if (NoDataToSave("No data to save.  The world must be running with tracked programmable blocks.")) return;

            Microsoft.Win32.SaveFileDialog saveFileDialog = new() { Filter = fileTypes };
            if (saveFileDialog.ShowDialog() != true) return;

            try
            {
                RefreshReportDataNow(); // Again, the world may have stopped while the dialog was open.
                if (NoDataToSave("No data to save.  The world stopped.")) return;
                Stopwatch sw = Stopwatch.StartNew();
                string report = GenerateTextReportForSave();
                sw.Stop();
                _ = SaveReportAsync(saveFileDialog.FileName, report, sw.Elapsed);
            }
            catch (Exception err)
            {
                ReportFailed(err);
            }
        }

        private void SaveCurrentReport_OnClick(object sender, RoutedEventArgs e)
        {
            if (NoDataToSave("No data to save.  Start the live view first.")) return;

            // Create the report first, so the time the save dialog stays open can't change the results.
            string report;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                report = GenerateTextReportForSave();
            }
            catch (Exception err)
            {
                ReportFailed(err);
                return;
            }
            sw.Stop();

            Microsoft.Win32.SaveFileDialog saveFileDialog = new() { Filter = fileTypes };
            if (saveFileDialog.ShowDialog() != true) return;
            _ = SaveReportAsync(saveFileDialog.FileName, report, sw.Elapsed);
        }

        /// <summary>True, after saying why, when the limiter is retired and has no data to show.</summary>
        private static bool ShowRetired()
        {
            if (!LimiterCore.Retired) return false;
            MessageBox.Show(LimiterCore.RetiredMessage, "Advanced PB Limiter", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }

        private bool NoDataToSave(string message)
        {
            if (ShowRetired()) return true;
            if (_allPlayers.Count > 0 && _allBlocks.Count > 0) return false;
            MessageBox.Show(message, "No Data", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }

        /// <summary>A click must not take the Torch window down, for example when the world stopped after the live view ran.</summary>
        private static void ReportFailed(Exception error)
        {
            Log.Error(error, "Could not generate the report");
            MessageBox.Show("Could not generate the report: " + error.Message, "Report Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>Writes the report and says where it went. It runs from a click, so it reports its own failure.</summary>
        private static async Task SaveReportAsync(string filePath, string report, TimeSpan generationTime)
        {
            try
            {
                using (StreamWriter writer = new(filePath, false))
                    await writer.WriteAsync(report);
                MessageBox.Show("Report saved to: " + filePath, Invariant($"Report Generation Took {generationTime.TotalMilliseconds:0.###}ms"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception err)
            {
                Log.Error(err, "Could not save the report to " + filePath);
                MessageBox.Show("Could not save the report: " + err.Message, "Report Not Saved", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GenerateTextReportForSave()
        {
            // Quick stats read the full snapshot, so resting blocks are counted even when the live grid hides them.
            CustomBlockReport? hottestPb = null;
            Tuple<double, ulong?, string?> hottestPlayer = new(0, null, null); // per tracked PB, playerSteamID, playerName
            Tuple<double, ulong?, string?> hottestPlayerAvg = new(0, null, null);
            Tuple<int, ulong?, string?> playerWithMostPBs = new(0, null, null); // amount, playerSteamID, playerName
            CustomBlockReport? pbWithMostOffences = null;
            CustomBlockReport? pbWithMostRecompiles = null;
            double serverTotalMS = 0;
            int serverBlockCount = 0;
            int serverTotalOffences = 0;

            foreach (CustomPlayerReport playerReport in _allPlayers.Values)
            {
                // Per tracked block, as chat and Quasar rank them; a player whose blocks all rest has no recent times to compare.
                CombinedRunTimes combined = playerReport.Combined;
                if (combined.TrackedBlocks > 0)
                {
                    if (hottestPlayer.Item2 is null || combined.LastRunTimeMSPerBlock > hottestPlayer.Item1)
                        hottestPlayer = new Tuple<double, ulong?, string?>(combined.LastRunTimeMSPerBlock, playerReport.SteamId, playerReport.PlayerName);

                    if (hottestPlayerAvg.Item2 is null || combined.RunTimeMSAvgPerBlock > hottestPlayerAvg.Item1)
                        hottestPlayerAvg = new Tuple<double, ulong?, string?>(combined.RunTimeMSAvgPerBlock, playerReport.SteamId, playerReport.PlayerName);
                }

                if (playerWithMostPBs.Item2 is null || playerReport.PbCount > playerWithMostPBs.Item1)
                    playerWithMostPBs = new Tuple<int, ulong?, string?>(playerReport.PbCount, playerReport.SteamId, playerReport.PlayerName);
            }

            int restingBlocks = 0;
            foreach (CustomBlockReport blockReport in _allBlocks.Values)
            {
                if (blockReport.Resting) restingBlocks++;

                // A resting block's run times may be an hour old: left out of the hottest block and the server average.
                if (!blockReport.Resting && (hottestPb is null || blockReport.LastRunTimeMS > hottestPb.LastRunTimeMS))
                    hottestPb = blockReport;

                if (pbWithMostOffences is null || blockReport.Offences > pbWithMostOffences.Offences)
                    pbWithMostOffences = blockReport;

                if (pbWithMostRecompiles is null || blockReport.Recompiles > pbWithMostRecompiles.Recompiles)
                    pbWithMostRecompiles = blockReport;

                if (!blockReport.Resting)
                {
                    serverTotalMS += blockReport.LastRunTimeMS;
                    serverBlockCount++;
                }

                serverTotalOffences += blockReport.Offences;
            }

            double serverAvgMs = serverBlockCount > 0 ? serverTotalMS / serverBlockCount : 0;

            StringBuilder sb = new();
            AppendRule(sb);
            sb.AppendLine("Advanced PB Limiter Report");
            AppendRule(sb);
            sb.AppendLine(Invariant($"Report generated at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}"));
            sb.AppendLine();

            AppendSection(sb, Invariant($"PER PROGRAMMABLE BLOCKS ({_allBlocks.Count} - {_allBlocks.Count - restingBlocks} active, {restingBlocks} resting)"));

            // Visible rows keep the grid's sort, then any resting blocks the grid hides are appended so the report always
            // lists every tracked block.
            int blockNumber = 0;
            HashSet<long> written = new();
            foreach (CustomBlockReport? item in PerPBDataGrid.Items)
            {
                if (item is null || !written.Add(item.EntityId)) continue;
                WriteBlockReport(sb, item, ++blockNumber);
            }

            foreach (CustomBlockReport item in _allBlocks.Values)
            {
                if (!written.Add(item.EntityId)) continue;
                WriteBlockReport(sb, item, ++blockNumber);
            }

            AppendSection(sb, Invariant($"PLAYERS ({_allPlayers.Count})"));
            int playerNumber = 0;
            foreach (CustomPlayerReport? item in PerPlayerDataGrid.Items)
            {
                if (item is null) continue;
                WritePlayerReport(sb, item, ++playerNumber);
            }

            AppendSection(sb, "QUICK STATS");
            AppendStat(sb, "Players tracked", Invariant($"{_allPlayers.Count}"));
            AppendStat(sb, "Programmable blocks",
                Invariant($"{_allBlocks.Count} ({_allBlocks.Count - restingBlocks} active, {restingBlocks} resting)"));
            AppendStat(sb, "Total offences", Invariant($"{serverTotalOffences}"));
            AppendStat(sb, "Server average last run", Invariant($"{serverAvgMs:0.########} ms"));
            AppendNote(sb, Invariant($"Average of {InvariantText.Count(serverBlockCount, "active block", "active blocks")}. Resting blocks are excluded, and blocks do not all run on the same tick, so treat this as a rough figure."));
            if (hottestPlayer.Item2 is ulong hottestPlayerId)
                AppendPlayerStat(sb, "Hottest player", hottestPlayerId, hottestPlayer.Item3, Invariant($"{hottestPlayer.Item1:0.########} ms per tracked PB"));
            else
                AppendStat(sb, "Hottest player", "None - no tracked PB has run lately");
            if (hottestPlayerAvg.Item2 is ulong hottestAveragePlayerId)
                AppendPlayerStat(sb, "Hottest player average", hottestAveragePlayerId, hottestPlayerAvg.Item3, Invariant($"{hottestPlayerAvg.Item1:0.########} ms per tracked PB"));
            else
                AppendStat(sb, "Hottest player average", "None - no tracked PB has run lately");
            if (playerWithMostPBs.Item2 is ulong busiestPlayerId)
                AppendPlayerStat(sb, "Player with most PBs", busiestPlayerId, playerWithMostPBs.Item3, InvariantText.Count(playerWithMostPBs.Item1, "PB", "PBs"));
            else
                AppendStat(sb, "Player with most PBs", "None");
            if (hottestPb is not null)
                AppendBlockStat(sb, "Hottest block", hottestPb, Invariant($"{hottestPb.LastRunTimeMS:0.########} ms"));
            else
                AppendStat(sb, "Hottest block", "None - no active block has run lately");
            if (pbWithMostRecompiles is not null)
                AppendBlockStat(sb, "Most recompiled block", pbWithMostRecompiles, InvariantText.Count(pbWithMostRecompiles.Recompiles, "recompile", "recompiles"));
            else
                AppendStat(sb, "Most recompiled block", "None");
            if (pbWithMostOffences is not null)
                AppendBlockStat(sb, "Block with most offences", pbWithMostOffences, InvariantText.Count(pbWithMostOffences.Offences, "offence", "offences"));
            else
                AppendStat(sb, "Block with most offences", "None");

            sb.AppendLine();
            AppendRule(sb);
            sb.AppendLine("End of report");
            AppendRule(sb);

            return sb.ToString();
        }

        /// <summary>A full width rule above and below the report's title and at its end.</summary>
        private static void AppendRule(StringBuilder sb) => sb.AppendLine(new string('=', ReportWidth));

        /// <summary>A section heading, underlined across the report width, with one blank line after it.</summary>
        private static void AppendSection(StringBuilder sb, string title)
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', ReportWidth));
            sb.AppendLine();
        }

        /// <summary>One labelled value under an entry, aligned with the other values in the same entry.</summary>
        private static void AppendField(StringBuilder sb, string label, string value)
        {
            string prefix = "    " + label.PadRight(13) + "  ";
            string indent = new(' ', prefix.Length);
            if (value.Length == 0)
            {
                sb.AppendLine(prefix);
                return;
            }

            bool first = true;
            foreach (string line in Wrap(value, Math.Max(16, ReportWidth - prefix.Length)))
            {
                sb.Append(first ? prefix : indent).AppendLine(line);
                first = false;
            }
        }

        /// <summary>The numbered heading of a block or player entry, with a long name wrapped under it.</summary>
        private static void AppendEntryTitle(StringBuilder sb, int number, string name)
        {
            bool first = true;
            foreach (string line in Wrap(Invariant($"[{number}] {name}"), ReportWidth - 4))
            {
                sb.Append(first ? "" : "    ").AppendLine(line);
                first = false;
            }
        }

        /// <summary>A labelled value in the quick stats, wrapped with the value's continuation lines aligned under it.</summary>
        private static void AppendStat(StringBuilder sb, string label, string value)
        {
            string prefix = label.PadRight(StatLabelWidth) + "  ";
            string indent = new(' ', prefix.Length);
            int width = Math.Max(24, ReportWidth - prefix.Length);
            bool first = true;
            foreach (string line in Wrap(value, width))
            {
                sb.Append(first ? prefix : indent).AppendLine(line);
                first = false;
            }
        }

        /// <summary>An explanatory paragraph under a stat, wrapped to the report width.</summary>
        private static void AppendNote(StringBuilder sb, string note)
        {
            foreach (string line in Wrap(note, ReportWidth - 4))
                sb.Append("    ").AppendLine(line);
        }

        /// <summary>A quick stat naming a block: the name on the label line, then its grid, owner and figure.</summary>
        private static void AppendBlockStat(StringBuilder sb, string label, CustomBlockReport block, string metric)
        {
            AppendStat(sb, label, block.BlockName ?? "(unnamed block)");
            AppendContinuation(sb, block.GridName);
            AppendContinuation(sb, Invariant($"{block.PlayerName} ({block.SteamId})"));
            AppendContinuation(sb, metric);
        }

        /// <summary>A quick stat naming a player: the name on the label line, then the Steam ID and figure.</summary>
        private static void AppendPlayerStat(StringBuilder sb, string label, ulong steamId, string? playerName, string metric)
        {
            AppendStat(sb, label, playerName ?? "Unknown");
            AppendContinuation(sb, Invariant($"{steamId}"));
            AppendContinuation(sb, metric);
        }

        /// <summary>A line continuing a stat's value, aligned under the first value line and wrapped to the report width.</summary>
        private static void AppendContinuation(StringBuilder sb, string value)
        {
            string indent = new(' ', StatLabelWidth + 2);
            foreach (string line in Wrap(value, ReportWidth - indent.Length))
                sb.Append(indent).AppendLine(line);
        }

        /// <summary>Breaks text into lines no longer than width, at a space where one is available.</summary>
        private static IEnumerable<string> Wrap(string value, int width)
        {
            while (value.Length > width)
            {
                int breakAt = value.LastIndexOf(' ', width);
                if (breakAt <= 0) breakAt = width;
                yield return value.Substring(0, breakAt).TrimEnd();
                value = value.Substring(breakAt).TrimStart();
            }

            if (value.Length > 0) yield return value;
        }

        private static void WriteBlockReport(StringBuilder sb, CustomBlockReport item, int number)
        {
            AppendEntryTitle(sb, number, item.BlockName ?? "(unnamed block)");
            AppendField(sb, "Player", item.PlayerName);
            AppendField(sb, "Steam ID", Invariant($"{item.SteamId}"));
            AppendField(sb, "Grid", item.GridName);
            AppendField(sb, "Status", item.Resting ? "Resting (not run lately)" : "Active");
            AppendField(sb, "Last run", Invariant($"{item.LastRunTimeMS:0.######}") + " ms");
            AppendField(sb, "Average", Invariant($"{item.AvgMS:0.######}") + " ms");
            AppendField(sb, "Offences", Invariant($"{item.Offences}"));
            AppendField(sb, "Recompiles", Invariant($"{item.Recompiles}"));
            AppendField(sb, "Memory", item.MemoryUsed);
            sb.AppendLine();
        }

        private static void WritePlayerReport(StringBuilder sb, CustomPlayerReport item, int number)
        {
            AppendEntryTitle(sb, number, item.PlayerName);
            AppendField(sb, "Steam ID", Invariant($"{item.SteamId}"));
            AppendField(sb, "Blocks", InvariantText.Count(item.PbCount, "block", "blocks"));
            AppendField(sb, "Combined last", Invariant($"{item.CombinedLastRunTimeMS:0.####}") + " ms");
            AppendField(sb, "Combined avg", Invariant($"{item.CombinedAvgMS:0.####}") + " ms");
            AppendField(sb, "Offences", Invariant($"{item.Offences}"));
            AppendField(sb, "Recompiles", Invariant($"{item.Recompiles}"));
            AppendField(sb, "Memory", item.MemoryUsed);
            sb.AppendLine();
        }

        private void TurnOff_PB(object sender, RoutedEventArgs e)
        {
            if (sender is not Button clickedbutton) return;
            if (clickedbutton.Tag is not MyProgrammableBlock pb) return;
            MySandboxGame? game = MySandboxGame.Static;
            if (game is null) return; // The world and its game have stopped.
            try
            {
                game.Invoke(() =>
                {
                    if (pb.Closed || pb.MarkedForClose || !pb.Enabled) return;
                    pb.Enabled = false;
                    TrackingManager.MarkStopped(pb);
                    Log.Info($"Request to turn off the programmable block [{pb.ReadName()}] from the plugin UI.");
                }, "Advanced PB Limiter");
            }
            catch (Exception error)
            {
                // A click must not take the Torch window down, including while the world unloads.
                Log.Error(error, "Could not turn off the programmable block from the plugin UI");
            }
        }

        private void TurnOff_AllPB(object sender, RoutedEventArgs e)
        {
            if (sender is not Button clickedbuttonAll) return;
            if (clickedbuttonAll.Tag is not TrackedPlayer player) return;
            MySandboxGame? game = MySandboxGame.Static;
            if (game is null) return;
            try
            {
                game.Invoke(() =>
                {
                    foreach (TrackedPBBlock? pb in player.GetAllPBBlocks())
                    {
                        if (pb?.ProgrammableBlock is null) continue;
                        if (pb.ProgrammableBlock.Closed || pb.ProgrammableBlock.MarkedForClose || !pb.ProgrammableBlock.Enabled) continue;
                        pb.ProgrammableBlock.Enabled = false;
                        pb.MarkStopped();
                    }
                }, "Advanced PB Limiter");
                Log.Info($"Request to turn off ALL the programmable blocks for [{player.PlayerName}] from the plugin UI.");
            }
            catch (Exception error)
            {
                Log.Error(error, "Could not turn off the player's programmable blocks from the plugin UI");
            }
        }
    }
}
