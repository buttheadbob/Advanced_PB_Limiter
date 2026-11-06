using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>One programmable block run, waiting to be processed. A struct, one of these is made on every run.</summary>
    public readonly struct TrackingDataUpdateRequest
    {
        public readonly MyProgrammableBlock ProgBlock;
        public readonly double RunTime;

        public TrackingDataUpdateRequest(MyProgrammableBlock progBlock, double runTime)
        {
            ProgBlock = progBlock;
            RunTime = runTime;
        }
    }
}
