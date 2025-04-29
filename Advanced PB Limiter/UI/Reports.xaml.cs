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
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using System.Windows.Forms;
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
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static readonly ConcurrentDictionary<ulong, CustomPlayerReport> PlayerReports = new();
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
            Dispatcher.Invoke(RefreshReportData);
        }
        
        private void RefreshReportData()
        {
            PlayerReports.Clear();
            BlockReports.Clear();
            PerPlayerDataGrid.ItemsSource = null;
            PerPBDataGrid.ItemsSource = null;
            
            double accumulativeLastRunTimeMs = 0;
            double avgMs = 0;
            double calculatedAvgMs = 0;
            int offences = 0;
            int recompiles = 0;
            long memoryUsed = 0;

            List<TrackedPlayer> trackedPlayer = TrackingManager.GetTrackedPlayerData();
            for (int index = 0; index < trackedPlayer.Count; index++)
            {
                for (int allpbIndex = 0; allpbIndex < trackedPlayer[index].GetAllPBBlocks.Count; allpbIndex++)
                {
                    if (trackedPlayer[index].GetAllPBBlocks[allpbIndex]?.ProgrammableBlock is null) continue;
                    accumulativeLastRunTimeMs += trackedPlayer[index].GetAllPBBlocks[allpbIndex].LastRunTimeMS;
                    avgMs += trackedPlayer[index].GetAllPBBlocks[allpbIndex].RunTimeMSAvg;
                    CustomBlockReport blockReport = new
                    (
                        trackedPlayer[index].SteamId, 
                        trackedPlayer[index].PlayerName ?? "Unknown", 
                        trackedPlayer[index].GetAllPBBlocks[allpbIndex].GridName, 
                        trackedPlayer[index].GetAllPBBlocks[allpbIndex]!.ProgrammableBlock!.CustomName.ToString(),
                        trackedPlayer[index].GetAllPBBlocks[allpbIndex].LastRunTimeMS, 
                        trackedPlayer[index].GetAllPBBlocks[allpbIndex].RunTimeMSAvg, 
                        trackedPlayer[index].GetAllPBBlocks[allpbIndex].PeekRunTimeMS,
                        trackedPlayer[index].GetAllPBBlocks[allpbIndex].Offences.Count,
                        trackedPlayer[index].GetAllPBBlocks[allpbIndex].Recompiles,
                        trackedPlayer[index].GetAllPBBlocks[allpbIndex].memoryUsage,
                        trackedPlayer[index].GetAllPBBlocks[allpbIndex].ProgrammableBlock
                    );
                    BlockReports.TryAdd(trackedPlayer[index].GetAllPBBlocks[allpbIndex].ProgrammableBlock!.EntityId, blockReport);
                    
                    recompiles += trackedPlayer[index].GetAllPBBlocks[allpbIndex].Recompiles;
                    offences += trackedPlayer[index].GetAllPBBlocks[allpbIndex].Offences.Count;
                    memoryUsed += trackedPlayer[index].GetAllPBBlocks[allpbIndex].memoryUsage;
                }

                if (trackedPlayer[index].PBBlockCount is not 0) // Should prevent NaN showing up in the live report.
                {
                    calculatedAvgMs = avgMs / trackedPlayer[index].PBBlockCount;
                }

                CustomPlayerReport report = new(trackedPlayer[index].SteamId, trackedPlayer[index].PlayerName ?? "Unknown", accumulativeLastRunTimeMs, calculatedAvgMs, offences, trackedPlayer[index].PBBlockCount, recompiles, memoryUsed, trackedPlayer[index]);
                PlayerReports.TryAdd(trackedPlayer[index].SteamId, report);

                accumulativeLastRunTimeMs = 0;
                avgMs = 0;
                calculatedAvgMs = 0;
                offences = 0;
                recompiles = 0;
                memoryUsed = 0;
                
                datagrid_BlockReports = new ObservableCollection<CustomBlockReport>(BlockReports.Values);
                datagrid_PlayerReports = new ObservableCollection<CustomPlayerReport>(PlayerReports.Values);
                PerPlayerDataGrid.ItemsSource = datagrid_PlayerReports;
                PerPBDataGrid.ItemsSource = datagrid_BlockReports;
            }
        }
        
        private void EnableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (!Advanced_PB_Limiter.GameOnline)
            {
                MessageBox.Show("Game is not online, cannot enable live view.", "Game Offline", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            if (double.TryParse(RefreshRateTextBox.Text.Replace(",","."), NumberStyles.Any, CultureInfo.InvariantCulture, out double refreshRate))
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
            if (PlayerReports.Count == 0 || BlockReports.Count == 0)
            {
                MessageBox.Show("No data to save.  Start the live view first.", "No Data", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            SaveFileDialog saveFileDialog = new();
            saveFileDialog.Filter = fileTypes;
            
            if (saveFileDialog.ShowDialog() != DialogResult.OK) return;
            RefreshReportData();
            
            Stopwatch sw = new();
            sw.Start();
            string report = await GenerateTextReportForSave();
            sw.Start();
            
            await WriteTextAsync(saveFileDialog.FileName, report);
            MessageBox.Show("Report saved to: " + saveFileDialog.FileName, $"Report Generation Took {sw.Elapsed.TotalMilliseconds}ms", MessageBoxButton.OK, MessageBoxImage.Information);
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
                string report = await GenerateTextReportForSave(); // Create the report first, so we don't have to wait for the save dialog to show causing the results to be different.
                sw.Stop();
                SaveFileDialog saveFileDialog = new();
                saveFileDialog.Filter = fileTypes;

                if (saveFileDialog.ShowDialog() != DialogResult.OK) return;
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
        
        private Task<string> GenerateTextReportForSave()
        {
            // Grab a snapshot of the data to prevent it from changing while we're working with it.
            KeyValuePair<ulong, CustomPlayerReport>[] playerReports = PlayerReports.ToArray();
            KeyValuePair<long, CustomBlockReport>[] blockReports = BlockReports.ToArray();
            ICollectionView perPBDataGrid = CollectionViewSource.GetDefaultView(PerPBDataGrid.ItemsSource);
            ICollectionView perPlayerDataGrid = CollectionViewSource.GetDefaultView(PerPlayerDataGrid.ItemsSource);
            
            // Quick Stats stuff
            Tuple<double, string?, ulong?, string?> hottestPb = new (0, null, null, null);  // ms, gridname, ownerid, ownername
            Tuple<double, ulong?> hottestPlayer = new (0, null);
            Tuple<double, ulong?> hottestPlayerAvg = new (0, null);
            Tuple<int, ulong?, string?> playerWithMostPBs = new (0, null, null); // amount, playerSteamID, playerName
            Tuple<int, string?, ulong?, string?> pbWithMostOffences = new (0, null, null, null); // offences, gridname, ownerid, ownername
            Tuple<int, ulong?> playerWithMostOffences = new (0, null); // amount, playerSteamID
            Tuple<int, string?, ulong?, string?> pbWithMostRecompiles = new (0, null, null, null); // recompiles, gridname, ownerid, ownername
            List<double> ServerTotalMS = new();
            int ServerTotalOffences = 0;
            
            foreach (KeyValuePair<ulong, CustomPlayerReport> playerReport in playerReports)
            {
                if (hottestPlayer.Item2 is null || playerReport.Value.CombinedLastRunTimeMS > hottestPlayer.Item1)
                    hottestPlayer = new Tuple<double, ulong?>(playerReport.Value.CombinedLastRunTimeMS, playerReport.Key);
                
                if (hottestPlayerAvg.Item2 is null || playerReport.Value.CombinedAvgMS > hottestPlayerAvg.Item1)
                    hottestPlayerAvg = new Tuple<double, ulong?>(playerReport.Value.CombinedAvgMS, playerReport.Key);
                
                if (playerWithMostOffences.Item2 is null || playerReport.Value.Offences > playerWithMostOffences.Item1)
                    playerWithMostOffences = new Tuple<int, ulong?>(playerReport.Value.Offences, playerReport.Key);

                if (playerWithMostPBs.Item2 is null || playerReport.Value.PbCount > playerWithMostPBs.Item1)
                    playerWithMostPBs = new Tuple<int, ulong?, string?>(playerReport.Value.PbCount, playerReport.Key, playerReport.Value.PlayerName);
            }
            
            foreach (KeyValuePair<long, CustomBlockReport> blockReport in blockReports)
            {
                if (hottestPb.Item4 is null || blockReport.Value.LastRunTimeMS > hottestPb.Item1)
                    hottestPb = new Tuple<double, string?, ulong?, string?>(blockReport.Value.LastRunTimeMS, blockReport.Value.GridName, blockReport.Value.SteamId, blockReport.Value.PlayerName);
                
                if (pbWithMostOffences.Item4 is null || blockReport.Value.Offences > pbWithMostOffences.Item1)
                    pbWithMostOffences = new Tuple<int, string?, ulong?, string?>(blockReport.Value.Offences, blockReport.Value.GridName, blockReport.Value.SteamId, blockReport.Value.PlayerName);
                
                if (pbWithMostRecompiles.Item4 is null || blockReport.Value.Recompiles > pbWithMostRecompiles.Item1)
                    pbWithMostRecompiles = new Tuple<int, string?, ulong?, string?>(blockReport.Value.Recompiles, blockReport.Value.GridName, blockReport.Value.SteamId, blockReport.Value.PlayerName);
                
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
            sb.AppendLine($"Hottest Block: {hottestPb.Item2} on {hottestPb.Item3} ({hottestPb.Item4}) with {hottestPb.Item1}ms");
            sb.AppendLine(pbWithMostRecompiles.Item4 is not null
                ? $"Most Recompiled Block: {pbWithMostRecompiles.Item2} on {pbWithMostRecompiles.Item3} ({pbWithMostRecompiles.Item4}) with {pbWithMostRecompiles.Item1} recompiles"
                : $"Most Recompiled Block: {pbWithMostRecompiles.Item2} with {pbWithMostRecompiles.Item1} recompiles");
            sb.AppendLine(pbWithMostOffences.Item4 is not null
                ? $"Block With Most Offenses: {pbWithMostOffences.Item2} on {pbWithMostOffences.Item3} ({pbWithMostOffences.Item4}) with {pbWithMostOffences.Item1} offences"
                : $"Block With Most Offenses: {pbWithMostOffences.Item2} with {pbWithMostOffences.Item1} offences");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("-----------------------");
            sb.AppendLine("End Of Report");
            sb.AppendLine("-----------------------");
            
            return Task.FromResult(sb.ToString());
        }
        
        private string PaddingGenerator(int input, int maxColumnWidth)
        {
            int padding = (maxColumnWidth - input) / 2;
            StringBuilder sb = new();
            for (int i = 0; i < padding; i++)
            {
                sb.Append(" ");
            }

            return sb.ToString();
        }

        private void TurnOff_PB(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button clickedbutton) return;
            if (clickedbutton.Tag is not MyProgrammableBlock pb) return;
            MySandboxGame.Static.Invoke(() => { pb.Enabled = false;},"Advanced PB Limiter");
            Log.Info($"Request to turn off the programmable block [{pb.CustomName}] from the plugin UI.");
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