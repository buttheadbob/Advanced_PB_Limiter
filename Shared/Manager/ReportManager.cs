#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.QuasarContracts;
using Advanced_PB_Limiter.Utils;
using Advanced_PB_Limiter.Utils.Reports;
using ExtensionMethods;
using Sandbox;
// ReSharper disable CommentTypo

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Builds the chat reports. The tracking data is gathered on the game thread, because it reads live game objects
    /// (a block's IsWorking, grid and block names), and the text is formatted on the thread pool afterwards.
    /// </summary>
    public static class ReportManager
    {
        private const int PLAYER_REPORT_COOLDOWN_SECONDS = 30;
        private const int MAX_SNAPSHOT_BLOCKS = 5000;
        private static readonly LimiterLog Log = new("Report Manager");
        // The most blocks a chat reply lists in full: Runtimes lists every sample of each, GetReport a few lines each.
        private const int MAX_RUNTIMES_BLOCKS = 10;
        private const int MAX_STATS_BLOCKS = 20;
        private const int MAX_REPORT_BLOCKS = 200;
        private static readonly ConcurrentDictionary<ulong, DateTime> LastPlayerReportRequest = new();
        private static readonly ConcurrentDictionary<long, DateTime> LastRuntimesRequest = new();

        public static async Task GetReportAsync(Action<string> respond)
        {
            List<PlayerReport> reports = await OnGameThread(GatherReports).ConfigureAwait(false);
            respond(FormatReport(reports));
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the game thread, where tracking data may read game objects, and returns its
        /// result to the pool thread that is formatting the report. The game thread is never blocked waiting for the
        /// report; the caller waits for this task on its own thread.
        /// </summary>
        private static Task<T> OnGameThread<T>(Func<T> work)
        {
            MySandboxGame? game = MySandboxGame.Static;
            if (game is null || !LimiterCore.GameOnline || Thread.CurrentThread == game.UpdateThread)
                return Task.FromResult(work());

            TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                game.Invoke(() =>
                {
                    try
                    {
                        completion.TrySetResult(work());
                    }
                    catch (Exception e)
                    {
                        completion.TrySetException(e);
                    }
                }, "Advanced_PB_Limiter");
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
            }
            return completion.Task;
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
                    IdentityId = player.PlayerId.ToString(CultureInfo.InvariantCulture),
                    SteamId = player.SteamId.ToString(CultureInfo.InvariantCulture),
                    Name = player.PlayerName ?? "Unknown",
                    IsNpc = player.IsNpc,
                    IsPrivileged = player.Privileged is not null
                };

                PlayerTotals totals = PlayerTotals.Of(player, (block, resting) =>
                {
                    if (snapshot.Blocks.Count >= MAX_SNAPSHOT_BLOCKS)
                    {
                        snapshot.Truncated = true;
                        return;
                    }

                    snapshot.Blocks.Add(new BlockRow
                    {
                        EntityId = block.ProgrammableBlock.EntityId.ToString(CultureInfo.InvariantCulture),
                        OwnerIdentityId = player.PlayerId.ToString(CultureInfo.InvariantCulture),
                        OwnerName = playerRow.Name,
                        GridName = block.CurrentGridName,
                        BlockName = block.ProgrammableBlock.ReadName(),
                        Enabled = block.ProgrammableBlock.Enabled,
                        LastRunTimeMS = block.LastRunTimeMS,
                        RunTimeMSAvg = block.RunTimeMSAvg,
                        PeekRunTimeMS = block.PeekRunTimeMS,
                        Offences = block.Offences.Count,
                        Recompiles = block.Recompiles,
                        MemoryBytes = block.memoryUsage,
                        MemoryUnmeasured = block.MemoryUnmeasured,
                        Resting = resting
                    });
                });

                playerRow.BlockCount = totals.Blocks;
                playerRow.Offences = totals.Offences;
                playerRow.Recompiles = totals.Recompiles;
                playerRow.MemoryBytes = totals.MemoryBytes;
                playerRow.MemoryUnmeasured = totals.MemoryUnmeasured;
                playerRow.CombinedLastRunTimeMS = totals.Combined.LastRunTimeMS;
                playerRow.CombinedRunTimeMSAvg = totals.Combined.RunTimeMSAvg;
                snapshot.Players.Add(playerRow);
            }

            return snapshot;
        }

        /// <summary>Game thread only: it reads live grid and block objects through PlayerTotals and each block's names.</summary>
        private static List<PlayerReport> GatherReports()
        {
            List<TrackedPlayer> players = TrackingManager.GetTrackedPlayerData();
            List<PlayerReport> reports = new(players.Count);
            foreach (TrackedPlayer player in players)
                reports.Add(GatherReport(player));
            return reports;
        }

        /// <summary>Game thread only: see GatherReports.</summary>
        private static PlayerReport GatherReport(TrackedPlayer player)
        {
            List<PBReport> pbReports = new();
            PlayerTotals totals = PlayerTotals.Of(player, (block, resting) => pbReports.Add(new PBReport(player.SteamId,
                block.CurrentGridName,
                block.ProgrammableBlock.ReadName(),
                block.LastRunTimeMS,
                block.RunTimeMSAvg,
                block.Offences.Count,
                block.Recompiles,
                resting)));

            return new PlayerReport(player.SteamId, player.PlayerId, player.PlayerName ?? "Unknown", player.IsNpc, pbReports, totals);
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

                CombinedRunTimes combined = playerReport.Combined();
                if (combined.TrackedBlocks == 0) continue; // All resting: no recent run times to compare.

                if (combined.LastRunTimeMSPerBlock > hottestPlayerMS)
                {
                    hottestPlayerMS = combined.LastRunTimeMSPerBlock;
                    hottestPlayer = playerReport.PlayerName;
                }

                if (combined.RunTimeMSAvgPerBlock > hottestPlayerAvgMS)
                {
                    hottestPlayerAvgMS = combined.RunTimeMSAvgPerBlock;
                    hottestPlayerAvg = playerReport.PlayerName;
                }
            }

            report.AppendLine();
            report.AppendLine(" >> Advanced PB Limiter Report");
            report.AppendLine("-----------------------------");
            report.AppendLine(" >  Overall Server Stats");
            report.Append("    -  Total Players: ").Append(InvariantText.Of(reports.Count)).AppendLine();
            report.Append("    -  Total Programmable Blocks Tracked: ").Append(InvariantText.Of(totalPbCount)).AppendLine();
            report.AppendLine();

            report.AppendLine(" >  Overall Player Stats");
            if (hottestPlayer is not null)
                report.Append("    -  Hottest Player Per Run: ").Append(hottestPlayer).AppendLine();
            if (hottestPlayerAvg is not null)
                report.Append("    -  Hottest Player Per Run Avg: ").Append(hottestPlayerAvg).AppendLine();
            if (playerWithMostPBs is not null)
                report.Append("    -  Player With Most PBs: ").Append(playerWithMostPBs).AppendLine();
            if (playerWithMostOffences is not null)
                report.Append("    -  Player With Most Offences: ").Append(playerWithMostOffences).AppendLine();
            if (playerWithMostOffensivePB is not null)
                report.Append("    -  Player With Most Offensive PB: ").Append(playerWithMostOffensivePB).AppendLine();
            if (playerWithMostRecompiledPB is not null)
                report.Append("    -  Player With Most Recompiled PB: ").Append(playerWithMostRecompiledPB).AppendLine();
            report.AppendLine();

            report.AppendLine(" >  Per Player Stats");
            int blocksListed = 0;
            foreach (PlayerReport playerReport in reports)
            {
                report.Append("    -  Player Name: ").Append(playerReport.PlayerName).AppendLine();
                if (playerReport.IsPrivileged) report.AppendLine("    -  Player is Privileged");
                report.AppendLine(playerReport.IsNpc
                    ? "    -  Player is NPC"
                    : $"    -  Player SteamID: {InvariantText.Of(playerReport.SteamId)}");
                report.Append("    -  Player PB Count: ").Append(InvariantText.Of(playerReport.GetPBReports.Count)).AppendLine();
                report.Append("    -  Player Total Offences: ").Append(InvariantText.Of(playerReport.TotalOffences)).AppendLine();
                report.Append("    -  Player Total Recompiles: ").Append(InvariantText.Of(playerReport.TotalRecompiles)).AppendLine();
                CombinedRunTimes playerCombined = playerReport.Combined();
                report.Append("    -  Player Combined Runtime: ").Append(InvariantText.Of(playerCombined.LastRunTimeMS, "0.0000")).AppendLine();
                report.Append("    -  Player Combined Runtime Avg: ").Append(InvariantText.Of(playerCombined.RunTimeMSAvg, "0.0000")).AppendLine();
                report.AppendLine("    >  Per Programmable Block Stats");

                foreach (PBReport pbReport in playerReport.GetPBReports)
                {
                    // The totals above count every block; the detail stops at a size a chat message can carry.
                    if (blocksListed++ >= MAX_REPORT_BLOCKS) break;
                    report.Append("       -  Grid Name: ").Append(pbReport.GridName).AppendLine();
                    report.Append("       -  Block Name: ").Append(pbReport.BlockName).AppendLine();
                    if (pbReport.Resting)
                        report.AppendLine("       -  Resting: not run lately, left out of the combined runtimes");
                    report.Append("       -  Block Last Runtime: ").Append(InvariantText.Of(pbReport.LastRunTimeMS, "0.0000")).AppendLine();
                    report.Append("       -  Block Runtime Avg: ").Append(InvariantText.Of(pbReport.RunTimeMSAvg, "0.0000")).AppendLine();
                    report.Append("       -  Block Offences: ").Append(InvariantText.Of(pbReport.Offences)).AppendLine();
                    report.Append("       -  Block Recompiles: ").Append(InvariantText.Of(pbReport.Recompiles)).AppendLine();
                    report.AppendLine();
                }
            }

            if (blocksListed > MAX_REPORT_BLOCKS)
                report.Append("    Only the first ").Append(InvariantText.Of(MAX_REPORT_BLOCKS)).AppendLine(" programmable blocks are listed; the totals count all of them.");
            report.AppendLine("-----------------------------");
            report.AppendLine("END OF REPORT");
            return report.ToString();
        }

        public static async Task PlayerStatsRequest(ulong steamId, Action<string> respond)
        {
            if (!Claim(LastPlayerReportRequest, steamId, DateTime.UtcNow))
            {
                respond($"You can only request player stats once every {InvariantText.Of(PLAYER_REPORT_COOLDOWN_SECONDS)} seconds.");
                return;
            }

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

            PlayerReport playerReport = await OnGameThread(() => GatherReport(player)).ConfigureAwait(false);
            StringBuilder report = new();
            report.AppendLine();
            report.AppendLine(" >> Advanced PB Limiter Player Report");
            report.AppendLine("-----------------------------");
            report.AppendLine(" >  Overall Player Stats");
            report.Append("    -  PB Count: ").Append(InvariantText.Of(playerReport.GetPBReports.Count)).AppendLine();
            report.Append("    -  Total Offences: ").Append(InvariantText.Of(playerReport.TotalOffences)).AppendLine();
            report.Append("    -  Total Recompiles: ").Append(InvariantText.Of(playerReport.TotalRecompiles)).AppendLine();
            CombinedRunTimes combinedTimes = playerReport.Combined();
            report.Append("    -  Combined Runtime: ").Append(InvariantText.Of(combinedTimes.LastRunTimeMS, "0.0000")).AppendLine();
            report.Append("    -  Combined Runtime Avg: ").Append(InvariantText.Of(combinedTimes.RunTimeMSAvg, "0.0000")).AppendLine();
            report.AppendLine();
            report.AppendLine(" >  Per Programmable Block Stats");
            int listed = 0;
            foreach (PBReport pbReport in playerReport.GetPBReports)
            {
                // The totals above count every block; the detail stops at a size a chat message can carry.
                if (listed++ >= MAX_STATS_BLOCKS) break;
                report.Append("    -  Grid Name: ").Append(pbReport.GridName).AppendLine();
                report.Append("    -  Block Name: ").Append(pbReport.BlockName).AppendLine();
                if (pbReport.Resting)
                    report.AppendLine("    -  Resting: not run lately, left out of the combined runtimes");
                report.Append("    -  Last Runtime: ").Append(InvariantText.Of(pbReport.LastRunTimeMS, "0.0000")).AppendLine();
                report.Append("    -  Runtime Avg: ").Append(InvariantText.Of(pbReport.RunTimeMSAvg, "0.0000")).AppendLine();
                report.Append("    -  Offences: ").Append(InvariantText.Of(pbReport.Offences)).AppendLine();
                report.Append("    -  Recompiles: ").Append(InvariantText.Of(pbReport.Recompiles)).AppendLine();
                report.AppendLine();
            }
            if (listed > MAX_STATS_BLOCKS)
                report.Append("    Only the first ").Append(InvariantText.Of(MAX_STATS_BLOCKS)).AppendLine(" programmable blocks are listed; the totals count all of them.");
            report.AppendLine("-----------------------------");
            report.AppendLine("END OF REPORT");
            respond(report.ToString());
            sw.Stop();
            Log.Info($"{playerReport.PlayerName} requested a player report, generated in {InvariantText.Of(sw.Elapsed.TotalMilliseconds, "0.0000")}ms.");
        }

        private const string NoBlocksText = "No programmable blocks found for you.  This could be because you have no programmable blocks or they are not running.";

        public static async Task PlayerRuntimesRequest(long identityId, Action<string> respond)
        {
            if (!Claim(LastRuntimesRequest, identityId, DateTime.UtcNow))
            {
                respond($"You can only request your runtimes once every {InvariantText.Of(PLAYER_REPORT_COOLDOWN_SECONDS)} seconds.");
                return;
            }

            TrackedPlayer? player = TrackingManager.GetTrackedPlayerDataById(identityId);
            if (player is null)
            {
                respond(NoBlocksText);
                return;
            }

            RuntimesSnapshot snapshot = await OnGameThread(() => GatherRuntimes(player)).ConfigureAwait(false);

            StringBuilder sb = new();
            foreach (RuntimeBlock block in snapshot.Blocks)
            {
                sb.Append("Grid Name: ").Append(block.GridName).AppendLine();
                sb.Append("Block Name: ").Append(block.BlockName).AppendLine();
                if (block.Resting)
                    sb.AppendLine("Resting: not run lately, these run times may be old");
                for (int index = 0; index < block.RunTimes.Length; index++)
                {
                    double runtime = block.RunTimes[index];
                    string outlierFlag = block.Outliers[index] ? " (outlier)" : "";
                    sb.Append(" - ").Append(InvariantText.Of(runtime, "0.0000")).Append("ms").Append(outlierFlag).AppendLine();
                }
                sb.AppendLine("-----------------------------");
            }

            if (snapshot.NotListed > 0)
                sb.Append("Only the first ").Append(InvariantText.Of(MAX_RUNTIMES_BLOCKS)).Append(" blocks are listed; ").Append(InvariantText.Count(snapshot.NotListed, "more is", "more are")).AppendLine(" not.");
            // Kept for a while after its last block went: nothing to show.
            respond(sb.Length > 0 ? sb.ToString() : NoBlocksText);
        }

        private sealed class RuntimeBlock
        {
            public RuntimeBlock(string gridName, string blockName, bool resting, double[] runTimes, bool[] outliers)
            {
                GridName = gridName;
                BlockName = blockName;
                Resting = resting;
                RunTimes = runTimes;
                Outliers = outliers;
            }

            public string GridName { get; }
            public string BlockName { get; }
            public bool Resting { get; }
            public double[] RunTimes { get; }
            public bool[] Outliers { get; }
        }

        private sealed class RuntimesSnapshot
        {
            public RuntimesSnapshot(List<RuntimeBlock> blocks, int notListed)
            {
                Blocks = blocks;
                NotListed = notListed;
            }

            public List<RuntimeBlock> Blocks { get; }
            public int NotListed { get; }
        }

        /// <summary>Game thread only: it reads live grid and block objects, and the samples while no run is changing them.</summary>
        private static RuntimesSnapshot GatherRuntimes(TrackedPlayer player)
        {
            List<RuntimeBlock> blocks = new();
            int listed = 0, notListed = 0;
            foreach ((TrackedPBBlock pbBlock, bool resting) in player.GetAllPBBlocksWithState())
            {
                if (listed++ >= MAX_RUNTIMES_BLOCKS)
                {
                    notListed++;
                    continue;
                }

                double[] runTimes = pbBlock.GetRunTimesMS;
                bool[] outliers = new bool[runTimes.Length];
                for (int index = 0; index < runTimes.Length; index++)
                    outliers[index] = pbBlock.IsOutlier(runTimes[index]);

                blocks.Add(new RuntimeBlock(pbBlock.CurrentGridName, pbBlock.ProgrammableBlock.ReadName(), resting, runTimes, outliers));
            }
            return new RuntimesSnapshot(blocks, notListed);
        }

        /// <summary>
        /// Takes this player's report slot if the cooldown has passed. Requests run on pool threads, so the check and the
        /// update are one atomic step: of several requests sent at once, one gets the slot.
        /// </summary>
        private static bool Claim<TKey>(ConcurrentDictionary<TKey, DateTime> requests, TKey player, DateTime now) where TKey : notnull
        {
            if (!requests.TryGetValue(player, out DateTime last))
                return requests.TryAdd(player, now);
            return (now - last).TotalSeconds >= PLAYER_REPORT_COOLDOWN_SECONDS && requests.TryUpdate(player, now, last);
        }

        public static void Clear()
        {
            LastPlayerReportRequest.Clear();
            LastRuntimesRequest.Clear();
        }
    }
}
