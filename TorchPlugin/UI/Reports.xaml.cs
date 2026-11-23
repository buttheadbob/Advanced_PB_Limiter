using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Data;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Utils;
using System.Windows.Media;
using Advanced_PB_Limiter.Utils.Reports;
using ExtensionMethods;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using MessageBox = System.Windows.MessageBox;
using Timer = System.Timers.Timer;
using static System.FormattableString;
// ReSharper disable SpecifyACultureInStringConversionExplicitly
// ReSharper disable TooWideLocalVariableScope

namespace Advanced_PB_Limiter.UI
{
    public partial class Reports
    {
        // Keyed by identity: every NPC has a SteamId of 0.
        private static readonly ConcurrentDictionary<long, CustomPlayerReport> PlayerReports = new();
        private static readonly ConcurrentDictionary<long, CustomBlockReport> BlockReports = new();
        private static readonly Timer _refreshTimer = new ();
        private const string fileTypes = "Text File (*.txt)|*.txt";
        private const int ColumnWidth = 30;
        private const double MinRefreshSeconds = 0.1;
        private const double MaxRefreshSeconds = 3600;
        private static readonly TorchLog Log = new("Advanced_PB_Limiter.UI.Reports");
        
        public Reports()
        {
            InitializeComponent();
            DataContext = this;
            _refreshTimer.Elapsed += TimedRefresh;
            ClearGrids();
        }

        private void ClearGrids()
        {
            PerPBDataGrid.ItemsSource = new ObservableCollection<CustomBlockReport>();
            PerPlayerDataGrid.ItemsSource = new ObservableCollection<CustomPlayerReport>();
        }

        /// <summary>Called from the game thread when the world unloads. Stops the live view and drops the stopped world's blocks.</summary>
        internal void OnSessionUnloading()
        {
            _ = Dispatcher.BeginInvoke(new Action(ClearAfterUnload));
        }

        private void ClearAfterUnload()
        {
            StopLiveView();
            PlayerReports.Clear();
            BlockReports.Clear();
            ClearGrids();
        }
        
        private void TimedRefresh(object sender, ElapsedEventArgs e)
        {
            _ = Dispatcher.BeginInvoke(new Action(RefreshReportData));
        }

        /// <summary>
        /// Fills the grids. Only a report read on the game thread may call a block's IsWorking, so a gather asked for on
        /// the UI thread is made on the game thread and the grids are filled back on the UI thread.
        /// </summary>
        private void RefreshReportData()
        {
            MySandboxGame? game = MySandboxGame.Static;
            if (game is null || Thread.CurrentThread == game.UpdateThread)
            {
                try
                {
                    ShowReportRows(GatherReportRows());
                }
                catch (Exception e)
                {
                    Log.Error(e, "Could not read the PB Limiter report");
                }
                return;
            }

            try
            {
                game.Invoke(() =>
                {
                    try
                    {
                        ReportRows rows = GatherReportRows();
                        if (Dispatcher is null) return; // The panel is gone.
                        _ = Dispatcher.BeginInvoke(new Action(() => ShowReportRows(rows)));
                    }
                    catch (Exception e)
                    {
                        // The game's update loop does not catch this action, so a report failure must not escape it.
                        Log.Error(e, "Could not read the PB Limiter report");
                    }
                }, "Advanced_PB_Limiter");
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not schedule the PB Limiter report");
            }
        }

        /// <summary>
        /// Fills the grids before the caller reads them, for the generated report. Runs where it is called on the game
        /// thread; on the UI thread it waits for the game thread, which is why it is only used from a click.
        /// </summary>
        private void RefreshReportDataNow()
        {
            MySandboxGame? game = MySandboxGame.Static;
            if (game is null || Thread.CurrentThread == game.UpdateThread)
            {
                ShowReportRows(GatherReportRows());
                return;
            }

            ReportRows? rows = null;
            Exception? error = null;
            using ManualResetEventSlim done = new(false);
            game.Invoke(() =>
            {
                try
                {
                    rows = GatherReportRows();
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    done.Set();
                }
            }, "Advanced_PB_Limiter");

            if (!done.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The game did not answer in time, so the report could not be read.");
            if (error is not null)
                throw new InvalidOperationException(error.Message, error);
            ShowReportRows(rows!);
        }

        /// <summary>Gathers the rows. Game thread only: it reads live blocks, their grids and their IsWorking.</summary>
        private static ReportRows GatherReportRows()
        {
            PlayerReports.Clear();
            BlockReports.Clear();
            ReportRows rows = new();

            foreach (TrackedPlayer player in TrackingManager.GetTrackedPlayerData())
            {
                PlayerTotals totals = PlayerTotals.Of(player, (block, resting) =>
                {
                    CustomBlockReport blockReport = new
                    (
                        player.SteamId,
                        player.PlayerName ?? "Unknown",
                        block.CurrentGridName,
                        block.ProgrammableBlock.ReadName(),
                        block.LastRunTimeMS,
                        block.RunTimeMSAvg,
                        block.PeekRunTimeMS,
                        block.Offences.Count,
                        block.Recompiles,
                        block.memoryUsage,
                        block.MemoryUnmeasured,
                        block.ProgrammableBlock,
                        resting: resting
                    );
                    BlockReports.TryAdd(block.ProgrammableBlock.EntityId, blockReport);
                    rows.Blocks.Add(blockReport);
                });

                CustomPlayerReport playerReport = new(player.SteamId, player.PlayerName ?? "Unknown", totals.Combined, totals.Offences, totals.Blocks, totals.Recompiles, totals.MemoryBytes, totals.MemoryUnmeasured, player);
                PlayerReports.TryAdd(player.PlayerId, playerReport);
                rows.Players.Add(playerReport);
            }

            return rows;
        }

        /// <summary>Shows gathered rows. UI thread only.</summary>
        private void ShowReportRows(ReportRows rows)
        {
            PerPlayerDataGrid.ItemsSource = new ObservableCollection<CustomPlayerReport>(rows.Players);
            PerPBDataGrid.ItemsSource = new ObservableCollection<CustomBlockReport>(rows.Blocks);
        }

        private sealed class ReportRows
        {
            public List<CustomPlayerReport> Players { get; } = new();
            public List<CustomBlockReport> Blocks { get; } = new();
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
                _refreshTimer.Interval = TimeSpan.FromSeconds(refreshRate).TotalMilliseconds;
                _refreshTimer.Start();
                RefreshRateTextBox.Background = new SolidColorBrush(Colors.Transparent);
                RefreshRateTextBox.Foreground = new SolidColorBrush(Colors.Aqua);
                EnableLiveViewButton.Background = new SolidColorBrush(Colors.RosyBrown);
                EnableLiveViewButton.Content = "Live View Active";
                return;
            }
            
            RefreshRateTextBox.Background = new SolidColorBrush(Colors.Red);
            RefreshRateTextBox.Foreground = new SolidColorBrush(Colors.Black);
            RefreshRateTextBox.Focus();
        }

        private void DisableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            StopLiveView();
        }

        private void StopLiveView()
        {
            _refreshTimer.Stop();
            EnableLiveViewButton.Background = new SolidColorBrush(Colors.DarkSlateGray);
            EnableLiveViewButton.Content = "Enable Live View";
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

        private static bool NoDataToSave(string message)
        {
            if (ShowRetired()) return true;
            if (!PlayerReports.IsEmpty && !BlockReports.IsEmpty) return false;
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
            // Grab a snapshot of the data to prevent it from changing while we're working with it.
            KeyValuePair<long, CustomPlayerReport>[] playerReports = PlayerReports.ToArray();
            KeyValuePair<long, CustomBlockReport>[] blockReports = BlockReports.ToArray();
            ICollectionView perPBDataGrid = CollectionViewSource.GetDefaultView(PerPBDataGrid.ItemsSource);
            ICollectionView perPlayerDataGrid = CollectionViewSource.GetDefaultView(PerPlayerDataGrid.ItemsSource);
            
            // Quick Stats stuff
            CustomBlockReport? hottestPb = null;
            Tuple<double, ulong?, string?> hottestPlayer = new (0, null, null); // per tracked PB, playerSteamID, playerName
            Tuple<double, ulong?, string?> hottestPlayerAvg = new (0, null, null);
            Tuple<int, ulong?, string?> playerWithMostPBs = new (0, null, null); // amount, playerSteamID, playerName
            CustomBlockReport? pbWithMostOffences = null;
            CustomBlockReport? pbWithMostRecompiles = null;
            List<double> ServerTotalMS = new();
            int ServerTotalOffences = 0;
            
            foreach (KeyValuePair<long, CustomPlayerReport> playerReport in playerReports)
            {
                // Per tracked block, as chat and Quasar rank them; a player whose blocks all rest has no recent times to compare.
                CombinedRunTimes combined = playerReport.Value.Combined;
                if (combined.TrackedBlocks > 0)
                {
                    if (hottestPlayer.Item2 is null || combined.LastRunTimeMSPerBlock > hottestPlayer.Item1)
                        hottestPlayer = new Tuple<double, ulong?, string?>(combined.LastRunTimeMSPerBlock, playerReport.Value.SteamId, playerReport.Value.PlayerName);

                    if (hottestPlayerAvg.Item2 is null || combined.RunTimeMSAvgPerBlock > hottestPlayerAvg.Item1)
                        hottestPlayerAvg = new Tuple<double, ulong?, string?>(combined.RunTimeMSAvgPerBlock, playerReport.Value.SteamId, playerReport.Value.PlayerName);
                }


                if (playerWithMostPBs.Item2 is null || playerReport.Value.PbCount > playerWithMostPBs.Item1)
                    playerWithMostPBs = new Tuple<int, ulong?, string?>(playerReport.Value.PbCount, playerReport.Value.SteamId, playerReport.Value.PlayerName);
            }
            
            foreach (KeyValuePair<long, CustomBlockReport> blockReport in blockReports)
            {
                // A resting block's run times may be an hour old: left out of the hottest block and the server average.
                if (!blockReport.Value.Resting && (hottestPb is null || blockReport.Value.LastRunTimeMS > hottestPb.LastRunTimeMS))
                    hottestPb = blockReport.Value;

                if (pbWithMostOffences is null || blockReport.Value.Offences > pbWithMostOffences.Offences)
                    pbWithMostOffences = blockReport.Value;

                if (pbWithMostRecompiles is null || blockReport.Value.Recompiles > pbWithMostRecompiles.Recompiles)
                    pbWithMostRecompiles = blockReport.Value;

                if (!blockReport.Value.Resting)
                    ServerTotalMS.Add(blockReport.Value.LastRunTimeMS);
                ServerTotalOffences += blockReport.Value.Offences;
            }


            StringBuilder sb = new();
            sb.AppendLine("Advanced PB Limiter Report");
            sb.AppendLine(Invariant($"Report generated at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}"));
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Per Programmable Block");
            sb.AppendLine("-----------------------");
            foreach (CustomBlockReport? item in perPBDataGrid) 
            {
                if (item is null) continue;
                Column(sb, "Player", item.PlayerName);
                Column(sb, "SteamID", Invariant($"{item.SteamId}"));
                Column(sb, "GridName", item.GridName);
                if (item.BlockName is not null)
                    Column(sb, "BlockName", item.BlockName);
                if (item.Resting)
                    Column(sb, "Resting", "not run lately");
                Column(sb, "LastRunTimeMS", Invariant($"{item.LastRunTimeMS}"));
                Column(sb, "AvgMS", Invariant($"{item.AvgMS}"));
                Column(sb, "Offences", Invariant($"{item.Offences}"));
                LastColumn(sb, "Recompiles", Invariant($"{item.Recompiles}"));
                LastColumn(sb, "MemoryUsed", item.MemoryUsed);
            }
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Per Player");
            sb.AppendLine("-----------------------");
            foreach (CustomPlayerReport? item in perPlayerDataGrid) 
            {
                if (item is null) continue;
                Column(sb, "Player", item.PlayerName);
                Column(sb, "SteamID", Invariant($"{item.SteamId}"));
                Column(sb, "CombinedRunTimeMS", Invariant($"{item.CombinedLastRunTimeMS}"));
                Column(sb, "CombinedAvgMS", Invariant($"{item.CombinedAvgMS}"));
                Column(sb, "Offences", Invariant($"{item.Offences}"));
                Column(sb, "Recompiles", Invariant($"{item.Recompiles}"));
                LastColumn(sb, "PBs", Invariant($"{item.PbCount}"));
                LastColumn(sb, "MemoryUsed", item.MemoryUsed);
            }
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Quick Stats");
            sb.AppendLine("-----------------------");
            double serverAvgMs = ServerTotalMS.Count > 0 ? ServerTotalMS.Sum() / ServerTotalMS.Count : 0;
            sb.AppendLine(Invariant($"Server Total Avg MS: {serverAvgMs}     ***NOTE: This is an average of the last run time of the tracked pbs, not those resting.  Not all pbs run on the same tick, keep this in mind!"));
            sb.AppendLine(hottestPlayer.Item2 is not null
                ? Invariant($"Hottest Player: {hottestPlayer.Item2} ({hottestPlayer.Item3}) with {hottestPlayer.Item1}ms per tracked PB")
                : "Hottest Player: none, no tracked PB has run lately");
            sb.AppendLine(hottestPlayerAvg.Item2 is not null
                ? Invariant($"Hottest Player Avg: {hottestPlayerAvg.Item2} ({hottestPlayerAvg.Item3}) with {hottestPlayerAvg.Item1}ms per tracked PB")
                : "Hottest Player Avg: none, no tracked PB has run lately");
            sb.AppendLine(playerWithMostPBs.Item2 is not null
                ? Invariant($"Player With Most PBs: {playerWithMostPBs.Item2} ({playerWithMostPBs.Item3}) with {InvariantText.Count(playerWithMostPBs.Item1, "PB", "PBs")}")
                : "Player With Most PBs: none");
            sb.AppendLine(Invariant($"Total Players Tracked: {playerReports.Length}"));
            sb.AppendLine(Invariant($"Total Programmable Blocks: {blockReports.Length}, {blockReports.Count(block => block.Value.Resting)} of them resting"));
            sb.AppendLine(Invariant($"Total Offences: {ServerTotalOffences}"));
            sb.AppendLine(Invariant($"Hottest Block: {DescribeBlock(hottestPb)} with {hottestPb?.LastRunTimeMS ?? 0}ms"));
            sb.AppendLine(Invariant($"Most Recompiled Block: {DescribeBlock(pbWithMostRecompiles)} with {InvariantText.Count(pbWithMostRecompiles?.Recompiles ?? 0, "recompile", "recompiles")}"));
            sb.AppendLine(Invariant($"Block With Most Offences: {DescribeBlock(pbWithMostOffences)} with {InvariantText.Count(pbWithMostOffences?.Offences ?? 0, "offence", "offences")}"));
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("-----------------------");
            sb.AppendLine("End Of Report");
            sb.AppendLine("-----------------------");
            
            return sb.ToString();
        }

        private static string DescribeBlock(CustomBlockReport? block)
        {
            return block is null ? "None" : Invariant($"{block.BlockName} on {block.GridName} owned by {block.PlayerName} ({block.SteamId})");
        }

        /// <summary>One column of a report row, centred in <see cref="ColumnWidth"/>, then the separator.</summary>
        private static void Column(StringBuilder sb, string label, string value)
        {
            string pad = ColumnPadding(label, value);
            sb.Append(pad).Append(label).Append(": ").Append(value).Append(pad).Append("||");
        }

        /// <summary>The last column of a report row: centred, then the end of the line.</summary>
        private static void LastColumn(StringBuilder sb, string label, string value)
        {
            sb.Append(ColumnPadding(label, value)).Append(label).Append(": ").AppendLine(value);
        }

        private static string ColumnPadding(string label, string value)
        {
            return new string(' ', Math.Max(0, (ColumnWidth - (label.Length + 2 + value.Length)) / 2));
        }

        private void TurnOff_PB(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button clickedbutton) return;
            if (clickedbutton.Tag is not MyProgrammableBlock pb) return;
            MySandboxGame.Static.Invoke(() =>
            {
                pb.Enabled = false;
                TrackingManager.MarkStopped(pb);
            }, "Advanced PB Limiter");
            Log.Info($"Request to turn off the programmable block [{pb.ReadName()}] from the plugin UI.");
        }

        private void TurnOff_AllPB(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button clickedbuttonAll) return;
            if (clickedbuttonAll.Tag is not TrackedPlayer player) return;
            MySandboxGame.Static.Invoke(() =>
            {
                foreach (TrackedPBBlock? pb in player.GetAllPBBlocks())
                {
                    if (pb?.ProgrammableBlock is null) continue;
                    pb.ProgrammableBlock.Enabled = false;
                    pb.MarkStopped();
                }
            }, "Advanced PB Limiter");
            Log.Info($"Request to turn off ALL the programmable blocks for [{player.PlayerName}] from the plugin UI.");
        }
    }
}