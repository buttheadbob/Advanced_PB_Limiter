using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Utils;
using Nexus;
using NLog;
using Sandbox.Game.World;
using Torch.Commands;
// ReSharper disable CommentTypo

namespace Advanced_PB_Limiter.Manager
{
    public static class ReportManager
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static ConcurrentDictionary<ulong, PlayerReport> LocalReports { get; } = new();
        private static ConcurrentDictionary<ulong, PlayerReport> NexusReports { get; } = new();
        private static bool NexusEnabled => Advanced_PB_Limiter.NexusInstalled;
        private static int ConnectedNexusServers => NexusManager.ConnectedNexusServers;
        private static readonly List<int> ReportedNexusServers = new();
        private static bool ReportGenerationInProgress { get; set; }
        private static List<NexusAPI.Server> NexusServers { get; set; } = new();
        private static readonly ConcurrentDictionary<ulong, DateTime> LastPlayerReportRequest = new();
        
        public static void AddOrUpdateReport(int fromServer, List<PlayerReport> report)
        {
            for (int index = 0; index < report.Count; index++)
            {
                if (NexusReports.TryGetValue(report[index].SteamId, out PlayerReport? playerNexusReport))
                {
                    playerNexusReport.AddPBReportRange(report[index].GetPBReports);
                }
                else
                {
                    LocalReports.TryAdd(report[index].SteamId, report[index]);
                }
            }

            ReportedNexusServers.Add(fromServer);
        }

        // ALWAYS RUN THIS ASYNC
        public static async Task GetReport(CommandContext? context, bool createNexusReport = false)
        {
            if (context is null)
            {
                Log.Warn("No requester context provided to create a report. Aborting...");
                return;
            }
            
            int waitCounter = 0;
            while (ReportGenerationInProgress)
            {
                await Task.Delay(250);
                waitCounter++;
                if (waitCounter > 20) // 250ms x 20 attempts = 5 seconds
                {
                    Log.Info($"Report generation already in progress... 5 second wait elapsed with no availability.  Aborting...");
                    context.Respond($"Report generator was busy with other requests.  Please try again in a few minutes.");
                    return;
                }
            }

            // Handle nexus generation.
            if (createNexusReport)
            {
                ReportGenerationInProgress = true;
                await NexusManager.RequestPlayerReports();
                await GenerateLocalReportData(); // Doesn't take long but can be done while waiting for reports to come in from nexus servers.

                int waitCount = 0;
                while (ReportedNexusServers.Count < ConnectedNexusServers)
                {
                    await Task.Delay(250);
                    waitCount++;
                    if (waitCount > 20) // 250ms x 20 attempts = 5 seconds
                    {
                        Log.Warn("Did not receive all reports from Nexus servers in time. Will report what we have...");
                        break;
                    }
                }
                
                context.Respond(await FormatReport());
                ReportedNexusServers.Clear();
                NexusReports.Clear();
                LocalReports.Clear();
                ReportGenerationInProgress = false;
                return;
            }
            
            // Handle local generation.
            ReportGenerationInProgress = true;
            await GenerateLocalReportData();
            context.Respond(await FormatReport());
            ReportedNexusServers.Clear();
            NexusReports.Clear();
            LocalReports.Clear();
            ReportGenerationInProgress = false;
        }

        private static Task<string> FormatReport()
        {
            if (Advanced_PB_Limiter.NexusInstalled)
                NexusServers = NexusManager.GetNexusServers();
            StringBuilder report = new();
           
            Tuple<double, string?, ulong?> hottestPb = new (0, null, null);  // ms, gridname, owner
            Tuple<double, ulong?> hottestPlayer = new (0, null);
            Tuple<double, ulong?> hottestPlayerAvg = new (0, null);
            Tuple<int, ulong?> playerWithMostPBs = new (0, null); // amount, playerSteamID
            Tuple<int, string?, ulong?> pbWithMostOffences = new (0, null, null); // offences, gridname, owner
            Tuple<int, ulong?> playerWithMostOffences = new (0, null); // amount, playerSteamID
            Tuple<int, string?, ulong?> pbWithMostRecompiles = new (0, null, null); // recompiles, gridname, owner
            int TotalPbCount = 0;
            
            // Nexus stuff
            Dictionary<int,double> ServerTotalMS = new();
            Dictionary<int,double> ServerTotalMSAvg = new();
            Dictionary<int,int> ServerTotalPBs = new();
            Dictionary<int,int> ServerTotalOffences = new();
            Dictionary<int,int> ServerTotalRecompiles = new();
            Tuple<double, NexusAPI.Server?> hottestServerMS = new (0, null);
            Tuple<double, NexusAPI.Server?> hottestServerMSAvg = new (0, null);
            Tuple<int, NexusAPI.Server?> serverWithMostPbs = new (0, null);
            Tuple<int, NexusAPI.Server?> serverWithMostOffences = new (0, null);
            Tuple<int, NexusAPI.Server?> serverWithMostRecompiles = new (0, null);
            
            // Merge local reports into nexus reports.. doesnt matter for local or nexus report creation.
            foreach (KeyValuePair<ulong,PlayerReport> localReport in LocalReports)
            {
                if (NexusReports.TryGetValue(localReport.Value.SteamId, out PlayerReport? nexusReportPlayer))
                {
                    localReport.Value.AddPBReportRange(nexusReportPlayer.GetPBReports);
                } else NexusReports.TryAdd(localReport.Value.SteamId, localReport.Value);
            }
            
            // First iterations to get hottest, etc
            foreach (KeyValuePair<ulong,PlayerReport> playerReport in NexusReports)
            {
                // Get player with most pb's
                if (playerReport.Value.GetPBReports.Count > playerWithMostPBs.Item1)
                    playerWithMostPBs = new Tuple<int, ulong?>(playerReport.Value.GetPBReports.Count, playerReport.Value.SteamId);
                
                // Get player with most offences
                if (playerReport.Value.TotalOffences > playerWithMostOffences.Item1)
                    playerWithMostOffences = new Tuple<int, ulong?>(playerReport.Value.TotalOffences, playerReport.Value.SteamId);
                
                double playersCombinedLastRunTimeMS = 0;
                double playersCombinedRunTimeMSAvg = 0;
                // Get pb stuff
                for (int index = 0; index < playerReport.Value.GetPBReports.Count; index++)
                {
                    TotalPbCount++;
                    // Get hottest pb
                    if (playerReport.Value.GetPBReports[index].LastRunTimeMS > hottestPb.Item1)
                        hottestPb = new Tuple<double, string?, ulong?>(playerReport.Value.GetPBReports[index].LastRunTimeMS, playerReport.Value.GetPBReports[index].GridName, playerReport.Value.GetPBReports[index].OwnerSteamId);
                    
                    // Get pb with most offences
                    if (playerReport.Value.GetPBReports[index].Offences > pbWithMostOffences.Item1)
                        pbWithMostOffences = new Tuple<int, string?, ulong?>(playerReport.Value.GetPBReports[index].Offences, playerReport.Value.GetPBReports[index].GridName, playerReport.Value.GetPBReports[index].OwnerSteamId);
                    
                    // Get pb with most recompiles
                    if (playerReport.Value.GetPBReports[index].Recompiles > pbWithMostRecompiles.Item1)
                        pbWithMostRecompiles = new Tuple<int, string?, ulong?>(playerReport.Value.GetPBReports[index].Recompiles, playerReport.Value.GetPBReports[index].GridName, playerReport.Value.GetPBReports[index].OwnerSteamId);
                    
                    // Get combined last run time
                    playersCombinedLastRunTimeMS += playerReport.Value.GetPBReports[index].LastRunTimeMS;
                    
                    // Get combined last run time avg
                    playersCombinedRunTimeMSAvg += playerReport.Value.GetPBReports[index].RunTimeMSAvg;
                    
                    // Server total ms
                    if (ServerTotalMS.TryGetValue(playerReport.Value.GetPBReports[index].ServerId, out double serverTotalMs))
                    {
                        ServerTotalMS[playerReport.Value.GetPBReports[index].ServerId] = serverTotalMs + playerReport.Value.GetPBReports[index].LastRunTimeMS;
                    }
                    else ServerTotalMS.Add(playerReport.Value.GetPBReports[index].ServerId, playerReport.Value.GetPBReports[index].LastRunTimeMS);
                    
                    // Server total ms avg
                    if (ServerTotalMSAvg.TryGetValue(playerReport.Value.GetPBReports[index].ServerId, out double serverTotalMsAvg))
                    {
                        ServerTotalMSAvg[playerReport.Value.GetPBReports[index].ServerId] = serverTotalMsAvg + playerReport.Value.GetPBReports[index].RunTimeMSAvg;
                    }
                    else ServerTotalMSAvg.Add(playerReport.Value.GetPBReports[index].ServerId, playerReport.Value.GetPBReports[index].RunTimeMSAvg);
                    
                    // Server total pbs
                    if (ServerTotalPBs.TryGetValue(playerReport.Value.GetPBReports[index].ServerId, out int serverTotalPbs))
                    {
                        ServerTotalPBs[playerReport.Value.GetPBReports[index].ServerId] = serverTotalPbs + 1;
                    }
                    else ServerTotalPBs.Add(playerReport.Value.GetPBReports[index].ServerId, 1);
                    
                    // Server total offences
                    if (ServerTotalOffences.TryGetValue(playerReport.Value.GetPBReports[index].ServerId, out int serverTotalOffences))
                    {
                        ServerTotalOffences[playerReport.Value.GetPBReports[index].ServerId] = serverTotalOffences + playerReport.Value.GetPBReports[index].Offences;
                    }
                    else ServerTotalOffences.Add(playerReport.Value.GetPBReports[index].ServerId, playerReport.Value.GetPBReports[index].Offences);
                    
                    // Server total recompiles
                    if (ServerTotalRecompiles.TryGetValue(playerReport.Value.GetPBReports[index].ServerId, out int serverTotalRecompiles))
                    {
                        ServerTotalRecompiles[playerReport.Value.GetPBReports[index].ServerId] = serverTotalRecompiles + playerReport.Value.GetPBReports[index].Recompiles;
                    }
                    else ServerTotalRecompiles.Add(playerReport.Value.GetPBReports[index].ServerId, playerReport.Value.GetPBReports[index].Recompiles);
                }
                
                // Get hottest player
                double calculatedCombinedLastRunTimeMs = playersCombinedLastRunTimeMS / playerReport.Value.GetPBReports.Count;
                if (calculatedCombinedLastRunTimeMs > hottestPlayer.Item1)
                    hottestPlayer = new Tuple<double, ulong?>(calculatedCombinedLastRunTimeMs, playerReport.Value.SteamId);
                
                // Get hottest player avg
                double calculatedCombinedRunTimeMsAvg = playersCombinedRunTimeMSAvg / playerReport.Value.GetPBReports.Count;
                if (calculatedCombinedRunTimeMsAvg > hottestPlayerAvg.Item1)
                    hottestPlayerAvg = new Tuple<double, ulong?>(calculatedCombinedRunTimeMsAvg, playerReport.Value.SteamId);
            }
            
            // Get hottest server
            foreach (KeyValuePair<int,double> serverTotalMs in ServerTotalMS)
            {
                if (!(serverTotalMs.Value > hottestServerMS.Item1)) continue;
                NexusAPI.Server? server = GetServerByID(serverTotalMs.Key);
                if (server is null) continue;
                hottestServerMS = new Tuple<double, NexusAPI.Server?>(serverTotalMs.Value, server);
            }
            
            // Get hottest server avg
            foreach (KeyValuePair<int,double> serverTotalMsAvg in ServerTotalMSAvg)
            {
                if (!(serverTotalMsAvg.Value > hottestServerMSAvg.Item1)) continue;
                NexusAPI.Server? server = GetServerByID(serverTotalMsAvg.Key);
                if (server is null) continue;
                hottestServerMSAvg = new Tuple<double, NexusAPI.Server?>(serverTotalMsAvg.Value, server);
            }
            
            // Get server with most pbs
            foreach (KeyValuePair<int,int> serverTotalPbs in ServerTotalPBs)
            {
                if (serverTotalPbs.Value <= serverWithMostPbs.Item1) continue;
                NexusAPI.Server? server = GetServerByID(serverTotalPbs.Key);
                if (server is null) continue;
                serverWithMostPbs = new Tuple<int, NexusAPI.Server?>(serverTotalPbs.Value, server);
            }
            
            // Get server with most offences
            foreach (KeyValuePair<int,int> serverTotalOffences in ServerTotalOffences)
            {
                if (serverTotalOffences.Value <= serverWithMostOffences.Item1) continue;
                NexusAPI.Server? server = GetServerByID(serverTotalOffences.Key);
                if (server is null) continue;
                serverWithMostOffences = new Tuple<int, NexusAPI.Server?>(serverTotalOffences.Value, server);
            }
            
            // Get server with most recompiles
            foreach (KeyValuePair<int,int> serverTotalRecompiles in ServerTotalRecompiles)
            {
                if (serverTotalRecompiles.Value <= serverWithMostRecompiles.Item1) continue;
                NexusAPI.Server? server = GetServerByID(serverTotalRecompiles.Key);
                if (server is null) continue;
                serverWithMostRecompiles = new Tuple<int, NexusAPI.Server?>(serverTotalRecompiles.Value, server);
            }
           
            report.AppendLine(); // Blank line so report starts on a fresh line, seperate from normal chat or other messages.
            report.AppendLine(" >> Advanced PB Limiter Report");
            report.AppendLine("-----------------------------");
            report.AppendLine(" >  Overall Server Stats");
            report.AppendLine($"    -  Total Nexus Servers: {ConnectedNexusServers}");
            report.AppendLine($"    -  Total Nexus Servers Reporting: {ReportedNexusServers.Count}");
            report.AppendLine($"    -  Total Players: {NexusReports.Count}");
            report.AppendLine($"    -  Total Programming Blocks Tracked: {TotalPbCount}");
            report.AppendLine($"    -  Hottest Server Per Run: {hottestServerMS.Item2?.Name}");
            report.AppendLine($"    -  Hottest Server Per Run Avg: {hottestServerMSAvg.Item2?.Name}");
            report.AppendLine($"    -  Server With Most PBs: {serverWithMostPbs.Item2?.Name}");
            report.AppendLine($"    -  Server With Most Offences: {serverWithMostOffences.Item2?.Name}");
            report.AppendLine($"    -  Server With Most Recompiles: {serverWithMostRecompiles.Item2?.Name}");
            report.AppendLine("");
            
            report.AppendLine($" >  Per Server Stats");
            foreach (NexusAPI.Server server in NexusServers)
            {
                report.AppendLine($"    -  Server Name: {server.Name}");
                report.AppendLine($"    -  Total MS: {ServerTotalMS[server.ServerID]:0.0000}");
                report.AppendLine($"    -  Total MS Avg: {ServerTotalMSAvg[server.ServerID]:0.0000}");
                report.AppendLine($"    -  Total PBs: {ServerTotalPBs[server.ServerID]}");
                report.AppendLine($"    -  Total Offences: {ServerTotalOffences[server.ServerID]}");
                report.AppendLine($"    -  Total Recompiles: {ServerTotalRecompiles[server.ServerID]}");
                report.AppendLine("");
            }
            
            report.AppendLine(" >  Overall Player Stats");
            report.AppendLine("    -  Hottest Player Per Run: " + (hottestPlayer.Item2 == null ? "" : MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)hottestPlayer.Item2)));
            if (hottestPlayerAvg.Item2 is not null)
                report.AppendLine("    -  Hottest Player Per Run Avg: " + (hottestPlayerAvg.Item2 == null ? "" : MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)hottestPlayerAvg.Item2)));
            if (playerWithMostPBs.Item2 is not null)
                report.AppendLine("    -  Player With Most PBs: " + (playerWithMostPBs.Item2 == null ? "" : MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)playerWithMostPBs.Item2)));
            if (playerWithMostOffences.Item2 is not null)
                report.AppendLine("    -  Player With Most Offences: " + (playerWithMostOffences.Item2 == null ? "" : MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)playerWithMostOffences.Item2)));
            if (pbWithMostRecompiles.Item3 is not null)
                report.AppendLine("    -  Player With Most Offensive PB: - " + (pbWithMostOffences.Item3 == null ? "" : MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)pbWithMostOffences.Item3)));
            if (pbWithMostRecompiles.Item3 is not null)
                report.AppendLine("    -  Player With Most Recompiled PB: " + (pbWithMostRecompiles.Item2 == null ? "" : MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)pbWithMostRecompiles.Item3)));
            report.AppendLine("");
            
            report.AppendLine(" >  Per Player Stats");
            foreach (KeyValuePair<ulong,PlayerReport> playerReport in NexusReports)
            {
                report.AppendLine($"    -  Player Name: {playerReport.Value.PlayerName}");
                if (playerReport.Value.IsPrivileged) report.AppendLine($"    -  Player is Privileged");
                report.AppendLine(MySession.Static.Players.IdentityIsNpc(playerReport.Value.PlayerId)
                    ? "    -  Player is NPC"
                    : $"    -  Player SteamID: {playerReport.Value.SteamId}");
                report.AppendLine($"    -  Player PB Count: {playerReport.Value.GetPBReports.Count}");
                report.AppendLine($"    -  Player Total Offences: {playerReport.Value.TotalOffences}");
                report.AppendLine($"    -  Player Total Recompiles: {playerReport.Value.TotalRecompiles}");
                report.AppendLine($"    -  Player Combined Runtime: {playerReport.Value.CombinedLastRunTimeMS:0.0000}");
                report.AppendLine($"    -  Player Combined Runtime Avg: {playerReport.Value.CombinedRunTimeMSAvg:0.0000}");
                report.AppendLine($"    >  Per Programmable Block Stats");
                for (int index = 0; index < playerReport.Value.GetPBReports.Count; index++)
                {
                    report.AppendLine($"       -  Grid Name: {playerReport.Value.GetPBReports[index].GridName}");
                    report.AppendLine($"       -  Block Name: {playerReport.Value.GetPBReports[index].BlockName}");
                    report.AppendLine($"       -  Grid Server: {GetServerByID(playerReport.Value.GetPBReports[index].ServerId)?.Name}");
                    report.AppendLine($"       -  Grid Last Runtime: {playerReport.Value.GetPBReports[index].LastRunTimeMS:0.0000}");
                    report.AppendLine($"       -  Grid Runtime Avg: {playerReport.Value.GetPBReports[index].RunTimeMSAvg:0.0000}");
                    report.AppendLine($"       -  Grid Offences: {playerReport.Value.GetPBReports[index].Offences}");
                    report.AppendLine($"       -  Grid Recompiles: {playerReport.Value.GetPBReports[index].Recompiles}");
                    report.AppendLine("");
                }
            
                report.AppendLine("-----------------------------");
                report.AppendLine("END OF REPORT");
            }
            return Task.FromResult(report.ToString());
        }
        
        private static Task GenerateLocalReportData()
        {
            List<TrackedPlayer> list = TrackingManager.GetTrackedPlayerData();
            for (int index = 0; index < list.Count; index++)
            {
                List<PBReport> pbReports = new();
                for (int ii = 0; ii < list[index].GetAllPBBlocks.Count; ii++)
                {
                    if (list[index].GetAllPBBlocks[ii] is null || list[index].GetAllPBBlocks[ii].ProgrammableBlock == null) continue;
                    
                    int myNexusId = 0;
                    if (NexusEnabled && NexusManager.ThisServer != null)    
                        myNexusId = NexusManager.ThisServer.ServerID;
                    
                    PBReport pbReport = new(list[index].SteamId,
                        list[index].GetAllPBBlocks[ii].ProgrammableBlock!.SlimBlock.CubeGrid.DisplayName,
                        list[index].GetAllPBBlocks[ii].ProgrammableBlock!.DisplayName,
                        myNexusId,
                        list[index].GetAllPBBlocks[ii].LastRunTimeMS,
                        list[index].GetAllPBBlocks[ii].RunTimeMSAvg,
                        list[index].GetAllPBBlocks[ii].Offences.Count,
                        list[index].GetAllPBBlocks[ii].Recompiles);
                    
                    pbReports.Add(pbReport);
                }
                PlayerReport playerReport = new(list[index].SteamId, list[index].PlayerId, list[index].PlayerName, true, pbReports);

                LocalReports.TryAdd(playerReport.SteamId, playerReport);
            }
            
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// This allows us to get the server object from the server id without polling nexus each time.  The server list gets updated on generation of the report.
        /// </summary>
        /// <param name="id">Server ID</param>
        /// <returns>The server or null if not found.</returns>
        private static NexusAPI.Server? GetServerByID(int id)
        {
            for (int index = 0; index < NexusServers.Count; index++)
            {
                if (NexusServers[index].ServerID == id)
                    return NexusServers[index];
            }

            return null;
        }

        public static async Task PlayerStatsRequest(CommandContext context)
        {
            if (!LastPlayerReportRequest.TryGetValue(context.Player.SteamUserId, out DateTime lastRequest))
                LastPlayerReportRequest.TryAdd(context.Player.SteamUserId, DateTime.Now);
            
            if (DateTime.Now.Subtract(lastRequest).TotalSeconds < 30)
            {
                context.Respond("You can only request player stats once every 30 seconds.");
                return;
            } 
            LastPlayerReportRequest[context.Player.SteamUserId] = DateTime.Now;

            int waitCounter = 0;
            while (ReportGenerationInProgress)
            {
                await Task.Delay(50); // Originally 250 but a simple report can be done in less than 10ms - 15ms.
                waitCounter++;
                if (waitCounter <= 20) continue; // 50ms x 100 attempts = 5 seconds
                context.Respond("The server has too many reports being generated at once.  Please try again in a few minutes.");
                return;
            }
            
            ReportGenerationInProgress = true;
            
            Stopwatch sw = Stopwatch.StartNew();
            await GenerateLocalReportData();
            if (LocalReports.TryGetValue(context.Player.SteamUserId, out PlayerReport playerReport))
            {
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
                for (int index = 0; index < playerReport.GetPBReports.Count; index++)
                {
                    report.AppendLine($"    -  Grid Name: {playerReport.GetPBReports[index].GridName}");
                    report.AppendLine($"    -  Block Name: {playerReport.GetPBReports[index].BlockName}");
                    report.AppendLine($"    -  Last Runtime: {playerReport.GetPBReports[index].LastRunTimeMS:0.0000}");
                    report.AppendLine($"    -  Runtime Avg: {playerReport.GetPBReports[index].RunTimeMSAvg:0.0000}");
                    report.AppendLine($"    -  Offences: {playerReport.GetPBReports[index].Offences}");
                    report.AppendLine($"    -  Recompiles: {playerReport.GetPBReports[index].Recompiles}");
                    report.AppendLine("");
                }
                report.AppendLine("-----------------------------");
                report.AppendLine("END OF REPORT");
                context.Respond(report.ToString());
                sw.Stop();
                Log.Info($"{playerReport.PlayerName} requested a player report, generated in {sw.Elapsed.TotalMilliseconds:0.0000}ms.");
                LocalReports.Clear();
                ReportGenerationInProgress = false;
                return;
            }
            
            context.Respond("No report data found for you.  This could be because you have no programmable blocks or they are not running.");
        }

        public static Task PlayerRuntimesRequest(CommandContext context)
        {
            StringBuilder sb = new();
            TrackedPlayer? player = TrackingManager.GetTrackedPlayerDataById(context.Player.IdentityId);
            
            if (player is null)
            {
                sb.AppendLine("No programmable blocks found for you.  This could be because you have no programmable blocks or they are not running.");
                context.Respond(sb.ToString());
                return Task.CompletedTask;
            }

            foreach (TrackedPBBlock pbBlock in player.GetAllPBBlocks)
            {
                sb.AppendLine($"Grid Name: {pbBlock.ProgrammableBlock!.SlimBlock.CubeGrid.DisplayName}");
                sb.AppendLine($"Block Name: {pbBlock.ProgrammableBlock.DisplayName}");
                foreach (double runtime in pbBlock.GetRunTimesMS)
                {
                    sb.AppendLine($" - {runtime:0.0000}ms");
                }               
                sb.AppendLine("-----------------------------");
            }
            
            context.Respond(sb.ToString());
            return Task.CompletedTask;
        }
    }
}