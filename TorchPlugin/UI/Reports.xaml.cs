using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Data;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Utils;
using System.Windows.Media;
using Advanced_PB_Limiter.Utils.Reports;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using MessageBox = System.Windows.MessageBox;
using Timer = System.Timers.Timer;
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
        private static ObservableCollection<CustomBlockReport> datagrid_BlockReports = new();
        private static ObservableCollection<CustomPlayerReport> datagrid_PlayerReports = new();
        private const string fileTypes = "Text File (*.txt)|*.txt";
        private static Logger Log = LogManager.GetCurrentClassLogger();
        
        public Reports()
        {
            InitializeComponent();
            DataContext = this;
            _refreshTimer.Elapsed += TimedRefreshAsync;
            PerPBDataGrid.ItemsSource = datagrid_BlockReports;
            PerPlayerDataGrid.ItemsSource = datagrid_PlayerReports;
        }
        
        private void TimedRefreshAsync(object sender, ElapsedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshReportData));
        }

        private void RefreshReportData()
        {
            PlayerReports.Clear();
            BlockReports.Clear();

            foreach (TrackedPlayer player in TrackingManager.GetTrackedPlayerData())
            {
                double accumulativeLastRunTimeMs = 0;
                double avgMs = 0;
                int offences = 0;
                int recompiles = 0;
                long memoryUsed = 0;

                List<TrackedPBBlock> blocks = player.GetAllPBBlocks;
                foreach (TrackedPBBlock block in blocks)
                {
                    double lastRunTimeMs = block.LastRunTimeMS;
                    double runTimeMsAvg = block.RunTimeMSAvg;
                    int blockOffences = block.Offences.Count;
                    long blockMemory = block.memoryUsage;

                    accumulativeLastRunTimeMs += lastRunTimeMs;
                    avgMs += runTimeMsAvg;
                    recompiles += block.Recompiles;
                    offences += blockOffences;
                    memoryUsed += blockMemory;

                    BlockReports.TryAdd(block.ProgrammableBlock.EntityId, new CustomBlockReport
                    (
                        player.SteamId,
                        player.PlayerName ?? "Unknown",
                        block.GridName,
                        ReadBlockName(block.ProgrammableBlock),
                        lastRunTimeMs,
                        runTimeMsAvg,
                        block.PeekRunTimeMS,
                        blockOffences,
                        block.Recompiles,
                        blockMemory,
                        block.ProgrammableBlock
                    ));
                }

                double calculatedAvgMs = blocks.Count > 0 ? avgMs / blocks.Count : 0; // Should prevent NaN showing up in the live report.
                PlayerReports.TryAdd(player.PlayerId, new CustomPlayerReport(player.SteamId, player.PlayerName ?? "Unknown", accumulativeLastRunTimeMs, calculatedAvgMs, offences, blocks.Count, recompiles, memoryUsed, player));
            }

            datagrid_BlockReports = new ObservableCollection<CustomBlockReport>(BlockReports.Values);
            datagrid_PlayerReports = new ObservableCollection<CustomPlayerReport>(PlayerReports.Values);
            PerPlayerDataGrid.ItemsSource = datagrid_PlayerReports;
            PerPBDataGrid.ItemsSource = datagrid_BlockReports;
        }

        /// <summary>
        /// This runs on the UI thread and the game edits a block's name in place on its own thread, so a script
        /// renaming its block can make this read throw. Rare, and the next refresh shows the name again.
        /// </summary>
        private static string ReadBlockName(MyProgrammableBlock block)
        {
            try
            {
                return block.CustomName.ToString();
            }
            catch (ArgumentOutOfRangeException)
            {
                return block.EntityId.ToString();
            }
        }

        private void EnableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (!LimiterCore.GameOnline)
            {
                MessageBox.Show("Game is not online, cannot enable live view.", "Game Offline", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (double.TryParse(RefreshRateTextBox.Text.Replace(",","."), NumberStyles.Any, CultureInfo.InvariantCulture, out double refreshRate) && refreshRate > 0)
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
            _refreshTimer.Stop();
            EnableLiveViewButton.Background = new SolidColorBrush(Colors.DarkSlateGray);
            EnableLiveViewButton.Content = "Enable Live View";
        }

        private void ManualUpdateButton_OnClick(object sender, RoutedEventArgs e)
        {
            RefreshReportData();
        }

        private async void GenerateTextReport_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (PlayerReports.Count == 0 || BlockReports.Count == 0)
                {
                    MessageBox.Show("No data to save.  Start the live view first.", "No Data", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Microsoft.Win32.SaveFileDialog saveFileDialog = new();
                saveFileDialog.Filter = fileTypes;

                if (saveFileDialog.ShowDialog() != true) return;
                RefreshReportData();

                Stopwatch sw = new();
                sw.Start();
                string report = GenerateTextReportForSave();
                sw.Stop();

                await WriteTextAsync(saveFileDialog.FileName, report);
                MessageBox.Show("Report saved to: " + saveFileDialog.FileName, $"Report Generation Took {sw.Elapsed.TotalMilliseconds}ms", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }

        private async void SaveCurrentReport_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (PlayerReports.Count == 0 || BlockReports.Count == 0)
                {
                    MessageBox.Show("No data to save.  Start the live view first.", "No Data", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                Stopwatch sw = new();
                sw.Start();
                string report = GenerateTextReportForSave(); // Create the report first, so we don't have to wait for the save dialog to show causing the results to be different.
                sw.Stop();
                Microsoft.Win32.SaveFileDialog saveFileDialog = new();
                saveFileDialog.Filter = fileTypes;

                if (saveFileDialog.ShowDialog() != true) return;
                await WriteTextAsync(saveFileDialog.FileName, report);
                MessageBox.Show("Report saved to: " + saveFileDialog.FileName, $"Report Generation Took {sw.Elapsed.TotalMilliseconds}ms", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }
        
        private static async Task WriteTextAsync(string filePath, string text)
        {
            using StreamWriter writer = new (filePath, false);
            await writer.WriteAsync(text);
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
            Tuple<double, ulong?> hottestPlayer = new (0, null);
            Tuple<double, ulong?> hottestPlayerAvg = new (0, null);
            Tuple<int, ulong?, string?> playerWithMostPBs = new (0, null, null); // amount, playerSteamID, playerName
            CustomBlockReport? pbWithMostOffences = null;
            Tuple<int, ulong?> playerWithMostOffences = new (0, null); // amount, playerSteamID
            CustomBlockReport? pbWithMostRecompiles = null;
            List<double> ServerTotalMS = new();
            int ServerTotalOffences = 0;
            
            foreach (KeyValuePair<long, CustomPlayerReport> playerReport in playerReports)
            {
                if (hottestPlayer.Item2 is null || playerReport.Value.CombinedLastRunTimeMS > hottestPlayer.Item1)
                    hottestPlayer = new Tuple<double, ulong?>(playerReport.Value.CombinedLastRunTimeMS, playerReport.Value.SteamId);

                if (hottestPlayerAvg.Item2 is null || playerReport.Value.CombinedAvgMS > hottestPlayerAvg.Item1)
                    hottestPlayerAvg = new Tuple<double, ulong?>(playerReport.Value.CombinedAvgMS, playerReport.Value.SteamId);

                if (playerWithMostOffences.Item2 is null || playerReport.Value.Offences > playerWithMostOffences.Item1)
                    playerWithMostOffences = new Tuple<int, ulong?>(playerReport.Value.Offences, playerReport.Value.SteamId);

                if (playerWithMostPBs.Item2 is null || playerReport.Value.PbCount > playerWithMostPBs.Item1)
                    playerWithMostPBs = new Tuple<int, ulong?, string?>(playerReport.Value.PbCount, playerReport.Value.SteamId, playerReport.Value.PlayerName);
            }
            
            foreach (KeyValuePair<long, CustomBlockReport> blockReport in blockReports)
            {
                if (hottestPb is null || blockReport.Value.LastRunTimeMS > hottestPb.LastRunTimeMS)
                    hottestPb = blockReport.Value;

                if (pbWithMostOffences is null || blockReport.Value.Offences > pbWithMostOffences.Offences)
                    pbWithMostOffences = blockReport.Value;

                if (pbWithMostRecompiles is null || blockReport.Value.Recompiles > pbWithMostRecompiles.Recompiles)
                    pbWithMostRecompiles = blockReport.Value;

                ServerTotalMS.Add(blockReport.Value.LastRunTimeMS);
                ServerTotalOffences += blockReport.Value.Offences;
            }

            
            int ColumnWidth = 30;
            
            StringBuilder sb = new();
            sb.AppendLine("Advanced PB Limiter Report");
            sb.AppendLine($"Report generated at {DateTime.Now}");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Per Programming Block");
            sb.AppendLine("-----------------------");
            foreach (CustomBlockReport? item in perPBDataGrid) 
            {
                if (item is null) continue;
                sb.Append($"{PaddingGenerator(8+item.PlayerName.Length, ColumnWidth)}Player: {item.PlayerName}{PaddingGenerator(8+item.PlayerName.Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(9+item.SteamId.ToString().Length, ColumnWidth)}SteamID: {item.SteamId}{PaddingGenerator(9+item.SteamId.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(10+item.GridName.Length, ColumnWidth)}GridName: {item.GridName}{PaddingGenerator(10+item.GridName.Length, ColumnWidth)}||");
                if (item.BlockName is not null)
                    sb.Append($"{PaddingGenerator(11+item.BlockName.Length, ColumnWidth)}BlockName: {item.BlockName}{PaddingGenerator(11+item.BlockName.Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(15+item.LastRunTimeMS.ToString().Length, ColumnWidth)}LastRunTimeMS: {item.LastRunTimeMS}{PaddingGenerator(15+item.LastRunTimeMS.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(7+item.AvgMS.ToString().Length, ColumnWidth)}AvgMS: {item.AvgMS}{PaddingGenerator(7+item.AvgMS.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(10+item.Offences.ToString().Length, ColumnWidth)}Offences: {item.Offences}{PaddingGenerator(10+item.Offences.ToString().Length, ColumnWidth)}||");
                sb.AppendLine($"{PaddingGenerator(12+item.Recompiles.ToString().Length, ColumnWidth)}Recompiles: {item.Recompiles}");
                sb.AppendLine($"{PaddingGenerator(12+item.MemoryUsed.Length, ColumnWidth)}MemoryUsed: {item.MemoryUsed}");
            }
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Per Player");
            sb.AppendLine("-----------------------");
            foreach (CustomPlayerReport? item in perPlayerDataGrid) 
            {
                if (item is null) continue;
                sb.Append($"{PaddingGenerator(8+item.PlayerName.Length, ColumnWidth)}Player: {item.PlayerName}{PaddingGenerator(8+item.PlayerName.Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(9+item.SteamId.ToString().Length, ColumnWidth)}SteamID: {item.SteamId}{PaddingGenerator(9+item.SteamId.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(19+item.CombinedLastRunTimeMS.ToString().Length, ColumnWidth)}CombinedRunTimeMS: {item.CombinedLastRunTimeMS}{PaddingGenerator(19+item.CombinedLastRunTimeMS.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(15+item.CombinedAvgMS.ToString().Length, ColumnWidth)}CombinedAvgMS: {item.CombinedAvgMS}{PaddingGenerator(15+item.CombinedAvgMS.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(10+item.Offences.ToString().Length, ColumnWidth)}Offences: {item.Offences}{PaddingGenerator(10+item.Offences.ToString().Length, ColumnWidth)}||");
                sb.Append($"{PaddingGenerator(12+item.Recompiles.ToString().Length, ColumnWidth)}Recompiles: {item.Recompiles}{PaddingGenerator(12+item.Recompiles.ToString().Length, ColumnWidth)}||");
                sb.AppendLine($"{PaddingGenerator(6+item.PbCount.ToString().Length, ColumnWidth)}PB's: {item.PbCount}");
                sb.AppendLine($"{PaddingGenerator(12+item.MemoryUsed.Length, ColumnWidth)}MemoryUsed: {item.MemoryUsed}");
            }
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Quick Stats");
            sb.AppendLine("-----------------------");
            sb.AppendLine("Server Total Avg MS: " + ServerTotalMS.Sum()/ServerTotalMS.Count + "     ***NOTE: This is an average of all the pb's last run time.  Not all pb's run on the same tick, keep this in mind!");
            sb.AppendLine(hottestPlayer.Item2 is not null
                ? $"Hottest Player: {hottestPlayer.Item2} ({MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)hottestPlayer.Item2)}) with {hottestPlayer.Item1}ms"
                : $"Hottest Player: {hottestPlayer.Item2} with {hottestPlayer.Item1}ms");
            sb.AppendLine(hottestPlayerAvg.Item2 is not null
                ? $"Hottest Player Avg: {hottestPlayerAvg.Item2} ({MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)hottestPlayerAvg.Item2)}) with {hottestPlayerAvg.Item1}ms"
                : $"Hottest Player Avg: {hottestPlayerAvg.Item2} with {hottestPlayerAvg.Item1}ms");
            sb.AppendLine(playerWithMostPBs.Item2 is not null
                ? $"Player With Most PBs: {playerWithMostPBs.Item2} ({playerWithMostPBs.Item3}) with {playerWithMostPBs.Item1} PBs" 
                : $"Player With Most PBs: {playerWithMostPBs.Item2} with {playerWithMostPBs.Item1} PBs");
            sb.AppendLine($"Total Players Tracked: {playerReports.Length}");
            sb.AppendLine($"Total Programmable Blocks Tracked: {blockReports.Length}");
            sb.AppendLine($"Total Offences: {ServerTotalOffences}");
            sb.AppendLine($"Hottest Block: {DescribeBlock(hottestPb)} with {hottestPb?.LastRunTimeMS ?? 0}ms");
            sb.AppendLine($"Most Recompiled Block: {DescribeBlock(pbWithMostRecompiles)} with {pbWithMostRecompiles?.Recompiles ?? 0} recompiles");
            sb.AppendLine($"Block With Most Offenses: {DescribeBlock(pbWithMostOffences)} with {pbWithMostOffences?.Offences ?? 0} offences");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("-----------------------");
            sb.AppendLine("End Of Report");
            sb.AppendLine("-----------------------");
            
            return sb.ToString();
        }

        private static string DescribeBlock(CustomBlockReport? block)
        {
            return block is null ? "None" : $"{block.BlockName} on {block.GridName} owned by {block.PlayerName} ({block.SteamId})";
        }

        private static string PaddingGenerator(int input, int maxColumnWidth)
        {
            return new string(' ', Math.Max(0, (maxColumnWidth - input) / 2));
        }

        private void TurnOff_PB(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button clickedbutton) return;
            if (clickedbutton.Tag is not MyProgrammableBlock pb) return;
            MySandboxGame.Static.Invoke(() => { pb.Enabled = false;},"Advanced PB Limiter");
            Log.Info($"Request to turn off the programmable block [{ReadBlockName(pb)}] from the plugin UI.");
        }

        private void TurnOff_AllPB(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button clickedbuttonAll) return;
            if (clickedbuttonAll.Tag is not TrackedPlayer player) return;
            MySandboxGame.Static.Invoke(() =>
            {
                foreach (TrackedPBBlock? pb in player.GetAllPBBlocks)
                {
                    if (pb?.ProgrammableBlock is null) continue;
                    pb.ProgrammableBlock.Enabled = false;
                }
            }, "Advanced PB Limiter");
            Log.Info($"Request to turn off ALL the programmable blocks for [{player.PlayerName}] from the plugin UI.");
        }
    }
}