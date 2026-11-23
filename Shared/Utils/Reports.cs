using System.Collections.Generic;

namespace Advanced_PB_Limiter.Utils.Reports
{
    public sealed class PlayerReport
    {
        public ulong SteamId { get; }
        public long PlayerId { get; }
        public string PlayerName { get; }
        public bool IsPrivileged { get; }
        public bool IsNpc { get; }
        private List<PBReport> PBReports { get; }
        private PlayerTotals Totals { get; }

        public PlayerReport(ulong steamId, long playerId, string playerName, bool isNpc, List<PBReport> pbReports, PlayerTotals totals)
        {
            Totals = totals;
            SteamId = steamId;
            PlayerId = playerId;
            PlayerName = playerName;
            IsNpc = isNpc;
            IsPrivileged = LimiterCore.Config.TryGetPrivilegedPlayer(steamId, out _);
            PBReports = pbReports;
        }

        public List<PBReport> GetPBReports => PBReports;

        public int TotalOffences => Totals.Offences;

        public int TotalRecompiles => Totals.Recompiles;

        /// <summary>The combined run times over this player's blocks, as every report counts them: see <see cref="PlayerTotals"/>.</summary>
        public CombinedRunTimes Combined() => Totals.Combined;
    }

    public sealed class PBReport
    {
        public ulong OwnerSteamId { get; }
        public string GridName { get; }
        public string BlockName { get; }
        public double LastRunTimeMS { get; }
        public double RunTimeMSAvg { get; }
        public int Offences { get; }
        public int Recompiles { get; }

        /// <summary>Resting between rare runs: dropped from tracking for being idle, kept to be tracked again on its next run.</summary>
        public bool Resting { get; }

        public PBReport(ulong ownerSteamId, string gridName, string blockName, double lastRunTimeMS, double runTimeMSAvg, int offences, int recompiles, bool resting)
        {
            Resting = resting;
            OwnerSteamId = ownerSteamId;
            GridName = gridName;
            BlockName = blockName;
            LastRunTimeMS = lastRunTimeMS;
            RunTimeMSAvg = runTimeMSAvg;
            Offences = offences;
            Recompiles = recompiles;
        }
    }
}
