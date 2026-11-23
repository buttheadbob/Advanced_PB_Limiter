using System;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils.Reports
{
    /// <summary>A row of the per block grid on the Reports tab.</summary>
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

        /// <summary>Resting between rare runs, not run lately: its run times may be an hour old and are left out of the totals.</summary>
        public bool Resting { get; }

        public CustomBlockReport(ulong steamId, string playerName, string gridName, string blockName, double lastRunTimeMs, double avgMs, double peekRunTimeMS, int offences, int recompiles, long memoryUsed, bool memoryUnmeasured, MyProgrammableBlock? progBlock, bool resting)
        {
            Resting = resting;
            SteamId = steamId;
            PlayerName = playerName;
            GridName = gridName;
            BlockName = blockName;
            LastRunTimeMS = Math.Round(lastRunTimeMs, 6, MidpointRounding.ToEven);
            AvgMS = Math.Round(avgMs, 6, MidpointRounding.ToEven);
            PeekRunTimeMS = peekRunTimeMS;
            Offences = offences;
            Recompiles = recompiles;
            MemoryUsed = MemoryText.Block(memoryUsed, memoryUnmeasured);
            pbBlock = progBlock;
        }
    }

    /// <summary>A row of the per player grid on the Reports tab.</summary>
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

        /// <summary>The combined run times, unrounded, as every report counts them; the hottest player is ranked by these.</summary>
        public CombinedRunTimes Combined { get; }

        public CustomPlayerReport(ulong steamId, string playerName, CombinedRunTimes combined, int offences, int pbCount, int recompiles, long memoryUsed, bool memoryUnmeasured, TrackedPlayer? playerTracked)
        {
            Combined = combined;
            SteamId = steamId;
            PlayerName = playerName;
            CombinedLastRunTimeMS = Math.Round(combined.LastRunTimeMS, 4, MidpointRounding.ToEven);
            CombinedAvgMS = Math.Round(combined.RunTimeMSAvg, 4, MidpointRounding.ToEven);
            Offences = offences;
            PbCount = pbCount;
            Recompiles = recompiles;
            MemoryUsed = MemoryText.Total(memoryUsed, memoryUnmeasured);
            PlayerTracked = playerTracked;
        }
    }
}
