using System.Collections.Generic;

namespace Advanced_PB_Limiter.Utils.Reports
{
    public sealed class PlayerReport
    {
        public ulong SteamId { get; init; }
        public long PlayerId { get; init; }
        public string PlayerName { get; init; }
        public bool IsPrivileged { get; init; }
        public bool IsNpc { get; init; }
        private List<PBReport> PBReports { get; init;}

        public PlayerReport(ulong steamId, long playerId, string playerName, bool isNpc, List<PBReport> pbReports)
        {
            SteamId = steamId;
            PlayerId = playerId;
            PlayerName = playerName;
            IsNpc = isNpc;
            IsPrivileged = LimiterCore.Config.TryGetPrivilegedPlayer(steamId, out _);
            PBReports = pbReports;
        }

        public List<PBReport> GetPBReports => PBReports;

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

    public record PBReport(ulong OwnerSteamId, string GridName, string BlockName, double LastRunTimeMS, double RunTimeMSAvg, int Offences, int Recompiles)
    {

    }
}
