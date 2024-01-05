using System.Collections.Generic;
using Advanced_PB_Limiter.Settings;
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable ForCanBeConvertedToForeach

namespace Advanced_PB_Limiter.Utils
{
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