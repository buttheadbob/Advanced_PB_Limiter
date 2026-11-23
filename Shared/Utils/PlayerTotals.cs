#nullable enable
using System;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// One player's totals over all their tracked blocks, resting ones included, as every report shows them: the block
    /// count, offences, recompiles, memory and the combined run times (see TrackedPlayer.CountsInCombined).
    /// </summary>
    public sealed class PlayerTotals
    {
        public CombinedRunTimes Combined { get; } = new();
        public int Blocks { get; private set; }
        public int Offences { get; private set; }
        public int Recompiles { get; private set; }
        /// <summary>The memory of the blocks the memory check could measure.</summary>
        public long MemoryBytes { get; private set; }

        /// <summary>At least one block could not be measured, see TrackedPBBlock.MemoryUnmeasured.</summary>
        public bool MemoryUnmeasured { get; private set; }

        /// <summary>Adds up the player's blocks, handing each to <paramref name="each"/> on the way, with whether it rests.</summary>
        public static PlayerTotals Of(TrackedPlayer player, Action<TrackedPBBlock, bool>? each = null)
        {
            PlayerTotals totals = new();
            foreach ((TrackedPBBlock block, bool resting) in player.GetAllPBBlocksWithState())
            {
                totals.Blocks++;
                totals.Offences += block.Offences.Count;
                totals.Recompiles += block.Recompiles;
                if (block.MemoryUnmeasured)
                    totals.MemoryUnmeasured = true;
                else
                    totals.MemoryBytes += block.memoryUsage;
                totals.Combined.Add(block.LastRunTimeMS, block.RunTimeMSAvg, player.CountsInCombined(block, resting));
                each?.Invoke(block, resting);
            }
            return totals;
        }
    }
}
