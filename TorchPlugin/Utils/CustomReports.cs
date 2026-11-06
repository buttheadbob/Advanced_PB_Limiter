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

        public CustomBlockReport(ulong steamId, string playerName, string gridName, string blockName, double lastRunTimeMs, double avgMs, double peekRunTimeMS, int offences, int recompiles, long memoryUsed, MyProgrammableBlock? progBlock)
        {
            SteamId = steamId;
            PlayerName = playerName;
            GridName = gridName;
            BlockName = blockName;
            LastRunTimeMS = Math.Round(lastRunTimeMs, 6);
            AvgMS = Math.Round(avgMs, 6);
            PeekRunTimeMS = peekRunTimeMS;
            Offences = offences;
            Recompiles = recompiles;
            MemoryUsed = HelperUtils.FormatBytesToKB(memoryUsed);
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
}
