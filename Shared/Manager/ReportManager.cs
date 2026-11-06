using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Advanced_PB_Limiter.QuasarContracts;
using Advanced_PB_Limiter.Utils;
using Advanced_PB_Limiter.Utils.Reports;
// ReSharper disable CommentTypo

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Builds the chat reports. These run off the game thread and every call works on its own snapshot of the
    /// tracking data, so any number can run at once and none can block another.
    /// </summary>
    public static class ReportManager
    {
        private const int PLAYER_REPORT_COOLDOWN_SECONDS = 30;
        private const int MAX_SNAPSHOT_BLOCKS = 5000;
        private static readonly LimiterLog Log = new("Report Manager");
        private static readonly ConcurrentDictionary<ulong, DateTime> LastPlayerReportRequest = new();

        public static void GetReport(Action<string> respond)
        {
            respond(FormatReport(GatherReports()));
        }

        /// <summary>The live report for Quasar. Call it on the game thread. A report stays a bounded size, whatever the server holds.</summary>
        public static ReportSnapshot BuildSnapshot()
        {
            ReportSnapshot snapshot = new()
            {
                Installed = true,
                Online = LimiterCore.GameOnline,
                Enabled = LimiterCore.Config.Enabled,
                Version = LimiterCore.Host.Version
            };

            foreach (TrackedPlayer player in TrackingManager.GetTrackedPlayerData())
            {
                PlayerRow playerRow = new()
                {
                    IdentityId = player.PlayerId,
                    SteamId = player.SteamId,
                    Name = player.PlayerName ?? "Unknown",
                    IsNpc = player.IsNpc,
                    IsPrivileged = player.Privileged is not null
                };

                foreach (TrackedPBBlock block in player.GetAllPBBlocks)
                {
                    int offences = block.Offences.Count;
                    playerRow.BlockCount++;
                    playerRow.CombinedLastRunTimeMS += block.LastRunTimeMS;
                    playerRow.CombinedRunTimeMSAvg += block.RunTimeMSAvg;
                    playerRow.Offences += offences;
                    playerRow.Recompiles += block.Recompiles;
                    playerRow.MemoryBytes += block.memoryUsage;

                    if (snapshot.Blocks.Count >= MAX_SNAPSHOT_BLOCKS)
                    {
                        snapshot.Truncated = true;
                        continue;
                    }

                    snapshot.Blocks.Add(new BlockRow
                    {
                        EntityId = block.ProgrammableBlock.EntityId,
                        OwnerIdentityId = player.PlayerId,
                        OwnerName = playerRow.Name,
                        GridName = block.ProgrammableBlock.CubeGrid?.DisplayName ?? block.GridName,
                        BlockName = block.ProgrammableBlock.CustomName.ToString(),
                        Enabled = block.ProgrammableBlock.Enabled,
                        LastRunTimeMS = block.LastRunTimeMS,
                        RunTimeMSAvg = block.RunTimeMSAvg,
                        PeekRunTimeMS = block.PeekRunTimeMS,
                        Offences = offences,
                        Recompiles = block.Recompiles,
                        MemoryBytes = block.memoryUsage
                    });
                }

                snapshot.Players.Add(playerRow);
            }

            return snapshot;
        }

        private static List<PlayerReport> GatherReports()
        {
            List<TrackedPlayer> players = TrackingManager.GetTrackedPlayerData();
            List<PlayerReport> reports = new(players.Count);
            foreach (TrackedPlayer player in players)
                reports.Add(GatherReport(player));
            return reports;
        }

        private static PlayerReport GatherReport(TrackedPlayer player)
        {
            List<TrackedPBBlock> playerBlocks = player.GetAllPBBlocks;
            List<PBReport> pbReports = new(playerBlocks.Count);
            foreach (TrackedPBBlock block in playerBlocks)
            {
                pbReports.Add(new PBReport(player.SteamId,
                    block.ProgrammableBlock.CubeGrid?.DisplayName ?? block.GridName,
                    block.ProgrammableBlock.CustomName.ToString(),
                    block.LastRunTimeMS,
                    block.RunTimeMSAvg,
                    block.Offences.Count,
                    block.Recompiles));
            }

            return new PlayerReport(player.SteamId, player.PlayerId, player.PlayerName ?? "Unknown", player.IsNpc, pbReports);
        }

        private static string FormatReport(List<PlayerReport> reports)
        {
            StringBuilder report = new();

            string? hottestPlayer = null;
            double hottestPlayerMS = 0;
            string? hottestPlayerAvg = null;
            double hottestPlayerAvgMS = 0;
            string? playerWithMostPBs = null;
            int mostPBs = 0;
            string? playerWithMostOffences = null;
            int mostOffences = 0;
            string? playerWithMostOffensivePB = null;
            int mostPBOffences = 0;
            string? playerWithMostRecompiledPB = null;
            int mostPBRecompiles = 0;
            int totalPbCount = 0;

            foreach (PlayerReport playerReport in reports)
            {
                List<PBReport> pbReports = playerReport.GetPBReports;
                if (pbReports.Count == 0) continue;

                if (pbReports.Count > mostPBs)
                {
                    mostPBs = pbReports.Count;
                    playerWithMostPBs = playerReport.PlayerName;
                }

                if (playerReport.TotalOffences > mostOffences)
                {
                    mostOffences = playerReport.TotalOffences;
                    playerWithMostOffences = playerReport.PlayerName;
                }

                foreach (PBReport pbReport in pbReports)
                {
                    totalPbCount++;

                    if (pbReport.Offences > mostPBOffences)
                    {
                        mostPBOffences = pbReport.Offences;
                        playerWithMostOffensivePB = playerReport.PlayerName;
                    }

                    if (pbReport.Recompiles > mostPBRecompiles)
                    {
                        mostPBRecompiles = pbReport.Recompiles;
                        playerWithMostRecompiledPB = playerReport.PlayerName;
                    }
                }

                double lastRunTimeMSPerBlock = playerReport.CombinedLastRunTimeMS / pbReports.Count;
                if (lastRunTimeMSPerBlock > hottestPlayerMS)
                {
                    hottestPlayerMS = lastRunTimeMSPerBlock;
                    hottestPlayer = playerReport.PlayerName;
                }

                double runTimeMSAvgPerBlock = playerReport.CombinedRunTimeMSAvg / pbReports.Count;
                if (runTimeMSAvgPerBlock > hottestPlayerAvgMS)
                {
                    hottestPlayerAvgMS = runTimeMSAvgPerBlock;
                    hottestPlayerAvg = playerReport.PlayerName;
                }
            }

            report.AppendLine();
            report.AppendLine(" >> Advanced PB Limiter Report");
            report.AppendLine("-----------------------------");
            report.AppendLine(" >  Overall Server Stats");
            report.AppendLine($"    -  Total Players: {reports.Count}");
            report.AppendLine($"    -  Total Programming Blocks Tracked: {totalPbCount}");
            report.AppendLine("");

            report.AppendLine(" >  Overall Player Stats");
            report.AppendLine("    -  Hottest Player Per Run: " + hottestPlayer);
            if (hottestPlayerAvg is not null)
                report.AppendLine("    -  Hottest Player Per Run Avg: " + hottestPlayerAvg);
            if (playerWithMostPBs is not null)
                report.AppendLine("    -  Player With Most PBs: " + playerWithMostPBs);
            if (playerWithMostOffences is not null)
                report.AppendLine("    -  Player With Most Offences: " + playerWithMostOffences);
            if (playerWithMostOffensivePB is not null)
                report.AppendLine("    -  Player With Most Offensive PB: " + playerWithMostOffensivePB);
            if (playerWithMostRecompiledPB is not null)
                report.AppendLine("    -  Player With Most Recompiled PB: " + playerWithMostRecompiledPB);
            report.AppendLine("");

            report.AppendLine(" >  Per Player Stats");
            foreach (PlayerReport playerReport in reports)
            {
                report.AppendLine($"    -  Player Name: {playerReport.PlayerName}");
                if (playerReport.IsPrivileged) report.AppendLine($"    -  Player is Privileged");
                report.AppendLine(playerReport.IsNpc
                    ? "    -  Player is NPC"
                    : $"    -  Player SteamID: {playerReport.SteamId}");
                report.AppendLine($"    -  Player PB Count: {playerReport.GetPBReports.Count}");
                report.AppendLine($"    -  Player Total Offences: {playerReport.TotalOffences}");
                report.AppendLine($"    -  Player Total Recompiles: {playerReport.TotalRecompiles}");
                report.AppendLine($"    -  Player Combined Runtime: {playerReport.CombinedLastRunTimeMS:0.0000}");
                report.AppendLine($"    -  Player Combined Runtime Avg: {playerReport.CombinedRunTimeMSAvg:0.0000}");
                report.AppendLine($"    >  Per Programmable Block Stats");

                foreach (PBReport pbReport in playerReport.GetPBReports)
                {
                    report.AppendLine($"       -  Grid Name: {pbReport.GridName}");
                    report.AppendLine($"       -  Block Name: {pbReport.BlockName}");
                    report.AppendLine($"       -  Grid Last Runtime: {pbReport.LastRunTimeMS:0.0000}");
                    report.AppendLine($"       -  Grid Runtime Avg: {pbReport.RunTimeMSAvg:0.0000}");
                    report.AppendLine($"       -  Grid Offences: {pbReport.Offences}");
                    report.AppendLine($"       -  Grid Recompiles: {pbReport.Recompiles}");
                    report.AppendLine("");
                }
            }

            report.AppendLine("-----------------------------");
            report.AppendLine("END OF REPORT");
            return report.ToString();
        }

        public static void PlayerStatsRequest(ulong steamId, Action<string> respond)
        {
            DateTime now = DateTime.UtcNow;
            if (LastPlayerReportRequest.TryGetValue(steamId, out DateTime lastRequest) && (now - lastRequest).TotalSeconds < PLAYER_REPORT_COOLDOWN_SECONDS)
            {
                respond($"You can only request player stats once every {PLAYER_REPORT_COOLDOWN_SECONDS} seconds.");
                return;
            }
            LastPlayerReportRequest[steamId] = now;

            Stopwatch sw = Stopwatch.StartNew();

            TrackedPlayer? player = null;
            foreach (TrackedPlayer trackedPlayer in TrackingManager.GetTrackedPlayerData())
            {
                if (trackedPlayer.SteamId != steamId) continue;
                player = trackedPlayer;
                break;
            }

            if (player is null)
            {
                respond("No report data found for you.  This could be because you have no programmable blocks or they are not running.");
                return;
            }

            PlayerReport playerReport = GatherReport(player);
            StringBuilder report = new();
            report.AppendLine();
            report.AppendLine(" >> Advanced PB Limiter Player Report");
            report.AppendLine("-----------------------------");
            report.AppendLine(" >  Overall Player Stats");
            report.AppendLine($"    -  PB Count: {playerReport.GetPBReports.Count}");
            report.AppendLine($"    -  Total Offences: {playerReport.TotalOffences}");
            report.AppendLine($"    -  Total Recompiles: {playerReport.TotalRecompiles}");
            report.AppendLine($"    -  Combined Runtime: {playerReport.CombinedLastRunTimeMS:0.0000}");
            report.AppendLine($"    -  Combined Runtime Avg: {playerReport.CombinedRunTimeMSAvg:0.0000}");
            report.AppendLine("");
            report.AppendLine(" >  Per Programmable Block Stats");
            foreach (PBReport pbReport in playerReport.GetPBReports)
            {
                report.AppendLine($"    -  Grid Name: {pbReport.GridName}");
                report.AppendLine($"    -  Block Name: {pbReport.BlockName}");
                report.AppendLine($"    -  Last Runtime: {pbReport.LastRunTimeMS:0.0000}");
                report.AppendLine($"    -  Runtime Avg: {pbReport.RunTimeMSAvg:0.0000}");
                report.AppendLine($"    -  Offences: {pbReport.Offences}");
                report.AppendLine($"    -  Recompiles: {pbReport.Recompiles}");
                report.AppendLine("");
            }
            report.AppendLine("-----------------------------");
            report.AppendLine("END OF REPORT");
            respond(report.ToString());
            sw.Stop();
            Log.Info($"{playerReport.PlayerName} requested a player report, generated in {sw.Elapsed.TotalMilliseconds:0.0000}ms.");
        }

        public static void PlayerRuntimesRequest(long identityId, Action<string> respond)
        {
            TrackedPlayer? player = TrackingManager.GetTrackedPlayerDataById(identityId);
            if (player is null)
            {
                respond("No programmable blocks found for you.  This could be because you have no programmable blocks or they are not running.");
                return;
            }

            StringBuilder sb = new();
            foreach (TrackedPBBlock pbBlock in player.GetAllPBBlocks)
            {
                sb.AppendLine($"Grid Name: {pbBlock.ProgrammableBlock.CubeGrid?.DisplayName ?? pbBlock.GridName}");
                sb.AppendLine($"Block Name: {pbBlock.ProgrammableBlock.DisplayNameText}");
                foreach (double runtime in pbBlock.GetRunTimesMS)
                {
                    string outlierFlag = pbBlock.IsOutlier(runtime) ? " (outlier)" : "";
                    sb.AppendLine($" - {runtime:0.0000}ms{outlierFlag}");
                }
                sb.AppendLine("-----------------------------");
            }

            respond(sb.ToString());
        }

        public static void Clear()
        {
            LastPlayerReportRequest.Clear();
        }
    }
}
