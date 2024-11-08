using System;
using System.Collections.Generic;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils.Reports
{
    public sealed class CustomBlockReport
    {
        public ulong SteamId { get; }
        public string PlayerName { get; }
        public string GridName { get; }
        public string? BlockName { get; }
        public double LastRunTimeMS { get; }
        public double AvgMS { get; }
        public double PeekRunTimeMS { get; }
        public int Offences { get; }
        public int Recompiles { get; }
        public string MemoryUsed { get; }
        public MyProgrammableBlock? pbBlock { get; }

        public CustomBlockReport(ulong steamId, string playerName, string gridName, string blockName, double lastRunTimeMs, double avgMs, double peekRunTimeMS, int offences, int recompiles, long memoryUsed, MyProgrammableBlock? progBlock)
        {
            SteamId = steamId;
            PlayerName = playerName;
            GridName = gridName;
            BlockName = blockName;
            LastRunTimeMS = Math.Round(lastRunTimeMs, 4);
            AvgMS = Math.Round(avgMs, 4);
            PeekRunTimeMS = peekRunTimeMS;
            Offences = offences;
            Recompiles = recompiles;
            MemoryUsed = HelperUtils.FormatBytesToKB(memoryUsed);
            pbBlock = progBlock;
        }
    }
    
    public sealed class CustomPlayerReport
    {
        public ulong SteamId { get; }
        public string PlayerName { get; }
        public double CombinedLastRunTimeMS { get; }
        public double CombinedAvgMS { get; }
        public int Offences { get; }
        public int PbCount { get; }
        public int Recompiles { get; }
        public string MemoryUsed { get; }
        public TrackedPlayer? PlayerTracked { get; }

        public CustomPlayerReport(ulong steamId, string playerName, double combinedLastRunTimeMs, double combinedAvgMs, int offences, int pbCount, int recompiles, long memoryUsed, TrackedPlayer? playerTracked)
        {
            SteamId = steamId;
            PlayerName = playerName;
            CombinedLastRunTimeMS = Math.Round(combinedLastRunTimeMs,4);
            CombinedAvgMS = Math.Round(combinedAvgMs, 4);
            Offences = offences;
            PbCount = pbCount;
            Recompiles = recompiles;
            MemoryUsed = HelperUtils.FormatBytesToKB(memoryUsed);
            PlayerTracked = playerTracked;
        }
    }
    
    public sealed class PlayerReport
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public ulong SteamId { get; init; }
        public long PlayerId { get; init; }
        public string PlayerName { get; init; }
        public bool IsPrivileged { get; init; }
        private List<PBReport> PBReports { get; init;}
        public bool IsNexusReport { get; init; }

        public PlayerReport(ulong steamId, long playerId, string playerName, bool isNexusReport, List<PBReport> pbReports)
        {
            SteamId = steamId;
            PlayerId = playerId;
            PlayerName = playerName;
            if (Config.PrivilegedPlayers.ContainsKey(steamId)) IsPrivileged = true;
            IsNexusReport = isNexusReport;
            PBReports = pbReports;
        }
        
        public List<PBReport> GetPBReports => PBReports;

        public void AddPBReport(PBReport report)
        {
            PBReports.Add(report);
        }
        
        public void AddPBReportRange(IEnumerable<PBReport> reports)
        {
            PBReports.AddRange(reports);
        }

        public int TotalOffences
        {
            get
            {
                int offences = 0;
                for (int index = 0; index < PBReports.Count; index++)
                {
                    offences += PBReports[index].Offences;
                }

                return offences;
            }
        }

        public int TotalRecompiles
        {
            get
            {
                int recompiles = 0;
                for (int index = 0; index < PBReports.Count; index++)
                {
                    recompiles += PBReports[index].Recompiles;
                }

                return recompiles;
            }
        }

        public double CombinedLastRunTimeMS
        {
            get
            {
                double total = 0;
                for (int index = 0; index < PBReports.Count; index++)
                {
                    total += PBReports[index].LastRunTimeMS;
                }
                
                return total;
            }
        }

        public double CombinedRunTimeMSAvg
        {
            get
            {
                double total = 0;
                for (int index = 0; index < PBReports.Count; index++)
                {
                    total += PBReports[index].RunTimeMSAvg;
                }
                
                return total;
            }
        }
    }
    
    public record PBReport(ulong OwnerSteamId, string GridName, string BlockName, int ServerId, double LastRunTimeMS, double RunTimeMSAvg, int Offences, int Recompiles)
    {
        
    }
}