using System.Collections.Generic;
using Advanced_PB_Limiter.Settings;
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable ForCanBeConvertedToForeach

namespace Advanced_PB_Limiter.Utils
{
    internal sealed class PlayerReport
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        internal ulong SteamId { get; init; }
        internal long PlayerId { get; init; }
        internal string PlayerName { get; init; }
        internal bool IsPrivileged { get; init; }
        private List<PBReport> PBReports { get; init;}
        internal bool IsNexusReport { get; init; }

        internal PlayerReport(ulong steamId, long playerId, string playerName, bool isNexusReport, List<PBReport> pbReports)
        {
            SteamId = steamId;
            PlayerId = playerId;
            PlayerName = playerName;
            if (Config.PrivilegedPlayers.ContainsKey(steamId)) IsPrivileged = true;
            IsNexusReport = isNexusReport;
            PBReports = pbReports;
        }
        
        internal List<PBReport> GetPBReports => PBReports;

        internal void AddPBReport(PBReport report)
        {
            PBReports.Add(report);
        }
        
        internal void AddPBReportRange(IEnumerable<PBReport> reports)
        {
            PBReports.AddRange(reports);
        }

        internal int TotalOffences
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

        internal int TotalRecompiles
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

        internal double CombinedLastRunTimeMS
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

        internal double CombinedRunTimeMSAvg
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
    
    internal record PBReport(ulong OwnerSteamId, string GridName, string BlockName, int ServerId, double LastRunTimeMS, double RunTimeMSAvg, int Offences, int Recompiles)
    {
        
    }
}