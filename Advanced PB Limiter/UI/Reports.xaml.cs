using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ConcurrentObservableCollections.ConcurrentObservableDictionary;
using System.Windows.Forms;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;
using Timer = System.Timers.Timer;

// ReSharper disable TooWideLocalVariableScope

namespace Advanced_PB_Limiter.UI
{
    public partial class Reports
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static readonly ConcurrentObservableDictionary<ulong, CustomPlayerReport> PlayerReports = new();
        private static readonly ConcurrentObservableDictionary<long, CustomBlockReport> BlockReports = new();
        private static readonly Timer _refreshTimer = new ();
        
        public Reports()
        {
            InitializeComponent();
            DataContext = this;
            _refreshTimer.Elapsed += (sender, args) => RefreshReportData();
        }
        
        private async Task Save()
        {
            await Advanced_PB_Limiter.Instance!.Save();
        }
        
        private Task RefreshReportData()
        {
            PlayerReports.Clear();
            BlockReports.Clear();
            
            double lastRunTimeMs = 0;
            double avgMs = 0;
            double calculatedLastRunTimeMs;
            double calculatedAvgMs;

            List<TrackedPlayer> data = TrackingManager.GetTrackedPlayerData();
            for (int index = 0; index < data.Count; index++)
            {
                for (int ii = 0; ii < data[index].GetAllPBBlocks.Count; ii++)
                {
                    if (data[index].GetAllPBBlocks[ii]?.ProgrammableBlock is null) continue;
                    lastRunTimeMs += data[index].GetAllPBBlocks[ii].LastRunTimeMS;
                    avgMs += data[index].GetAllPBBlocks[ii].RunTimeMSAvg;
                    CustomBlockReport blockReport = new
                    (
                        data[index].SteamId, 
                        data[index].PlayerName, 
                        data[index].GetAllPBBlocks[ii]!.ProgrammableBlock!.CubeGrid.DisplayName, 
                        data[index].GetAllPBBlocks[ii]!.ProgrammableBlock!.DisplayName,
                        data[index].GetAllPBBlocks[ii].LastRunTimeMS, 
                        data[index].GetAllPBBlocks[ii].RunTimeMSAvg, 
                        data[index].GetAllPBBlocks[ii].Offences,
                        data[index].GetAllPBBlocks[ii].Recompiles
                    );
                    BlockReports.TryAdd(data[index].GetAllPBBlocks[ii].ProgrammableBlock!.EntityId, blockReport);
                }

                calculatedAvgMs = avgMs / data[index].PBBlockCount;
                calculatedLastRunTimeMs = lastRunTimeMs / data[index].PBBlockCount;

                CustomPlayerReport report = new(data[index].SteamId, data[index].PlayerName, calculatedLastRunTimeMs, calculatedAvgMs, data[index].Offences, data[index].PBBlockCount);
                PlayerReports.TryAdd(data[index].SteamId, report);

                lastRunTimeMs = 0;
                avgMs = 0;
            }

            return Task.CompletedTask;
        }

        private sealed class CustomPlayerReport
        {
            public ulong SteamId { get; }
            public string PlayerName { get; }
            public double CombinedLastRunTimeMS { get; }
            public double CombinedAvgMS { get; }
            public int Offences { get; }
            public int PbCount { get; }

            public CustomPlayerReport(ulong steamId, string playerName, double combinedLastRunTimeMs, double combinedAvgMs, int offences, int pbCount)
            {
                SteamId = steamId;
                PlayerName = playerName;
                CombinedLastRunTimeMS = combinedLastRunTimeMs;
                CombinedAvgMS = combinedAvgMs;
                Offences = offences;
                PbCount = pbCount;
            }
        }
        
        private sealed class CustomBlockReport
        {
            public ulong SteamId { get; }
            public string PlayerName { get; }
            public string GridName { get; }
            public string BlockName { get; }
            public double LastRunTimeMS { get; }
            public double AvgMS { get; }
            public int Offences { get; }
            public int Recompiles { get; }

            public CustomBlockReport(ulong steamId, string playerName, string gridName, string blockName, double lastRunTimeMs, double avgMs, int offences, int recompiles)
            {
                SteamId = steamId;
                PlayerName = playerName;
                GridName = gridName;
                BlockName = blockName;
                LastRunTimeMS = lastRunTimeMs;
                AvgMS = avgMs;
                Offences = offences;
                Recompiles = recompiles;
            }
        }

        private void EnableLiveViewButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (float.TryParse(RefreshRateTextBox.Text, out float refreshRate))
            {
                _refreshTimer.Interval = refreshRate * 1000;
                _refreshTimer.Start();
                RefreshRateTextBox.Background = new SolidColorBrush(Colors.Transparent);
                RefreshRateTextBox.Foreground = new SolidColorBrush(Colors.Aqua);
                EnableLiveViewButton.Background = new SolidColorBrush(Colors.RosyBrown);
                EnableLiveViewButton.Content = "Live View Active";
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

        private async void ManualUpdateButton_OnClick(object sender, RoutedEventArgs e)
        {
            await RefreshReportData();
        }

        private async void GenerateTextReport_OnClick(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new();
            saveFileDialog.Filter = "Text File (*.txt)|*.txt";

            if (saveFileDialog.ShowDialog() != DialogResult.OK) return;
            await RefreshReportData();
            string report = await GenerateTextReportForSave();
            await WriteTextAsync(saveFileDialog.FileName, report);
            MessageBox.Show("Report saved to: " + saveFileDialog.FileName, "Report Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void SaveCurrentReport_OnClick(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new();
            saveFileDialog.Filter = "Text File (*.txt)|*.txt";

            if (saveFileDialog.ShowDialog() != DialogResult.OK) return;
            string report = await GenerateTextReportForSave();
            await WriteTextAsync(saveFileDialog.FileName, report);
            MessageBox.Show("Report saved to: " + saveFileDialog.FileName, "Report Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        
        private async Task WriteTextAsync(string filePath, string text)
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
                if (playerReport.Value.CombinedLastRunTimeMS > hottestPlayer.Item1)
                    hottestPlayer = new Tuple<double, ulong?>(playerReport.Value.CombinedLastRunTimeMS, playerReport.Key);
                
                if (playerReport.Value.CombinedAvgMS > hottestPlayerAvg.Item1)
                    hottestPlayerAvg = new Tuple<double, ulong?>(playerReport.Value.CombinedAvgMS, playerReport.Key);
                
                if (playerReport.Value.Offences > playerWithMostOffences.Item1)
                    playerWithMostOffences = new Tuple<int, ulong?>(playerReport.Value.Offences, playerReport.Key);

                if (playerReport.Value.PbCount > playerWithMostPBs.Item1)
                    playerWithMostPBs = new Tuple<int, ulong?, string?>(playerReport.Value.PbCount, playerReport.Key, playerReport.Value.PlayerName);
            }
            
            foreach (KeyValuePair<long, CustomBlockReport> blockReport in blockReports)
            {
                if (blockReport.Value.LastRunTimeMS > hottestPb.Item1)
                    hottestPb = new Tuple<double, string?, ulong?, string?>(blockReport.Value.LastRunTimeMS, blockReport.Value.GridName, blockReport.Value.SteamId, blockReport.Value.PlayerName);
                
                if (blockReport.Value.Offences > pbWithMostOffences.Item1)
                    pbWithMostOffences = new Tuple<int, string?, ulong?, string?>(blockReport.Value.Offences, blockReport.Value.GridName, blockReport.Value.SteamId, blockReport.Value.PlayerName);
                
                if (blockReport.Value.Recompiles > pbWithMostRecompiles.Item1)
                    pbWithMostRecompiles = new Tuple<int, string?, ulong?, string?>(blockReport.Value.Recompiles, blockReport.Value.GridName, blockReport.Value.SteamId, blockReport.Value.PlayerName);
                
                ServerTotalMS.Add(blockReport.Value.LastRunTimeMS);
                ServerTotalOffences += blockReport.Value.Offences;
            }

            // Neat formatting, cause your a nerd!
            // Get max column width
            int maxColumnFound = 0;
            // Check pb data widths
            foreach (CustomBlockReport? item in perPBDataGrid) 
            {
                if (item is null) continue;
                if (item.PlayerName.Length > maxColumnFound) maxColumnFound = item.PlayerName.Length;
                if (item.SteamId.ToString().Length > maxColumnFound) maxColumnFound = item.SteamId.ToString().Length;
                if (item.GridName.Length > maxColumnFound) maxColumnFound = item.GridName.Length;
                if (item.BlockName.Length > maxColumnFound) maxColumnFound = item.BlockName.Length;
                // Doubt any ms or offence values will be longer than 10 SteamID, but just in case.
            }
            // Check player data widths
            foreach (CustomPlayerReport? item in perPlayerDataGrid) 
            {
                if (item is null) continue;
                if (item.PlayerName.Length > maxColumnFound) maxColumnFound = item.PlayerName.Length;
                if (item.SteamId.ToString().Length > maxColumnFound) maxColumnFound = item.SteamId.ToString().Length;
                // Doubt any ms or offence values will be longer than 10 SteamID, but just in case.
            }
            // Add 26 for the longest label and a bit
            maxColumnFound += 26;
            
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
                sb.AppendLine($"{PaddingGenerator(8+item.PlayerName.Length, maxColumnFound)}Player: {item.PlayerName}{PaddingGenerator(8+item.PlayerName.Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(9+item.SteamId.ToString().Length, maxColumnFound)}SteamID: {item.SteamId}{PaddingGenerator(9+item.SteamId.ToString().Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(10+item.GridName.Length, maxColumnFound)}GridName: {item.GridName}{PaddingGenerator(10+item.GridName.Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(11+item.BlockName.Length, maxColumnFound)}BlockName: {item.BlockName}{PaddingGenerator(11+item.BlockName.Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(15+item.LastRunTimeMS.ToString(CultureInfo.InvariantCulture).Length, maxColumnFound)}LastRunTimeMS: {item.LastRunTimeMS}{PaddingGenerator(15+item.LastRunTimeMS.ToString(CultureInfo.InvariantCulture).Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(7+item.AvgMS.ToString(CultureInfo.InvariantCulture).Length, maxColumnFound)}AvgMS: {item.AvgMS}{PaddingGenerator(7+item.AvgMS.ToString(CultureInfo.InvariantCulture).Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(10+item.Offences.ToString().Length, maxColumnFound)}Offences: {item.Offences}");
            }
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Per Player");
            sb.AppendLine("-----------------------");
            foreach (CustomPlayerReport? item in perPlayerDataGrid) 
            {
                if (item is null) continue;
                sb.AppendLine($"{PaddingGenerator(8+item.PlayerName.Length, maxColumnFound)}Player: {item.PlayerName}{PaddingGenerator(8+item.PlayerName.Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(9+item.SteamId.ToString().Length, maxColumnFound)}SteamID: {item.SteamId}{PaddingGenerator(9+item.SteamId.ToString().Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(23+item.CombinedLastRunTimeMS.ToString(CultureInfo.InvariantCulture).Length, maxColumnFound)}CombinedLastRunTimeMS: {item.CombinedLastRunTimeMS}{PaddingGenerator(23+item.CombinedLastRunTimeMS.ToString(CultureInfo.InvariantCulture).Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(15+item.CombinedAvgMS.ToString(CultureInfo.InvariantCulture).Length, maxColumnFound)}CombinedAvgMS: {item.CombinedAvgMS}{PaddingGenerator(15+item.CombinedAvgMS.ToString(CultureInfo.InvariantCulture).Length, maxColumnFound)}||");
                sb.Append($"{PaddingGenerator(10+item.Offences.ToString().Length, maxColumnFound)}Offences: {item.Offences}");
            }
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("> Quick Stats");
            sb.AppendLine("-----------------------");
            sb.AppendLine("Server Total Avg MS: " + ServerTotalMS.Sum()/ServerTotalMS.Count + "     ***NOTE: This is an average of all the pb's last run time.  Not all pb's run on the same tick, keep this in mind!");
            sb.AppendLine("Hottest Player: " + hottestPlayer.Item2 + " with " + hottestPlayer.Item1 + "ms");
            sb.AppendLine("Hottest Player Avg: " + hottestPlayerAvg.Item2 + " with " + hottestPlayerAvg.Item1 + "ms");
            sb.AppendLine("Player With Most PBs: " + playerWithMostPBs.Item2 + " (" + playerWithMostPBs.Item3 + ") with " + playerWithMostPBs.Item1 + " PBs");
            sb.AppendLine("Total Players Tracked: " + playerReports.Length);
            sb.AppendLine("Total Programmable Blocks Tracked: " + blockReports.Length);
            sb.AppendLine("Total Offences: " + ServerTotalOffences);
            sb.AppendLine("Hottest Block: " + hottestPb.Item2 + " on " + hottestPb.Item3 + " (" + hottestPb.Item4 + ") with " + hottestPb.Item1 + "ms");
            sb.AppendLine("Most Recompiled Block: " + pbWithMostRecompiles.Item2 + " on " + pbWithMostRecompiles.Item3 + " (" + pbWithMostRecompiles.Item4 + ") with " + pbWithMostRecompiles.Item1 + " recompiles");
            sb.AppendLine("Block With Most Offenses: " + pbWithMostOffences.Item2 + " on " + pbWithMostOffences.Item3 + " (" + pbWithMostOffences.Item4 + ") with " + pbWithMostOffences.Item1 + " offences");
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
    }
}