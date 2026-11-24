#nullable enable

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// One player's combined run times, counted by the same rules for the combined limits and every report: over the
    /// blocks that count, see TrackedPlayer.CountsInCombined for the limits and CountsInCombinedForReport for reports.
    /// Reports use the state cached on the game thread, so their totals can trail the limits by up to one cleanup pass.
    /// The hottest player is the one with the highest of these per counted block.
    /// </summary>
    public sealed class CombinedRunTimes
    {
        public double LastRunTimeMS { get; private set; }
        public double RunTimeMSAvg { get; private set; }

        /// <summary>The blocks counted.</summary>
        public int TrackedBlocks { get; private set; }

        /// <summary>Adds a block's run times when it counts, see TrackedPlayer.CountsInCombined.</summary>
        public void Add(double lastRunTimeMS, double runTimeMSAvg, bool counts)
        {
            if (!counts) return;
            LastRunTimeMS += lastRunTimeMS;
            RunTimeMSAvg += runTimeMSAvg;
            TrackedBlocks++;
        }

        /// <summary>What the hottest player is ranked by. 0 when every block rests.</summary>
        public double LastRunTimeMSPerBlock => TrackedBlocks > 0 ? LastRunTimeMS / TrackedBlocks : 0;

        /// <summary>As <see cref="LastRunTimeMSPerBlock"/>, for the average.</summary>
        public double RunTimeMSAvgPerBlock => TrackedBlocks > 0 ? RunTimeMSAvg / TrackedBlocks : 0;
    }
}
