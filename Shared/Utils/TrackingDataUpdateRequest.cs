using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>One programmable block run, waiting to be processed. A struct, one of these is made on every run.</summary>
    public readonly struct TrackingDataUpdateRequest
    {
        public readonly MyProgrammableBlock ProgBlock;
        public readonly double RunTime;

        /// <summary>A garbage collection happened during the run, see RunClock: the instant kill leaves it to the ordinary limits.</summary>
        public readonly bool Collected;

        public TrackingDataUpdateRequest(MyProgrammableBlock progBlock, double runTime, bool collected)
        {
            ProgBlock = progBlock;
            RunTime = runTime;
            Collected = collected;
        }
    }
}
