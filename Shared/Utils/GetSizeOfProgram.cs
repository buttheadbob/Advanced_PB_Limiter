#nullable enable
using Sandbox.ModAPI;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>The memory a script holds, see GetSize.</summary>
    public static class ProgramSize
    {
        /// <param name="program">The script to measure.</param>
        /// <param name="limit">Stop once the size is over this, in bytes. 0 for no limit.</param>
        /// <param name="time">The Stopwatch ticks the scan took.</param>
        /// <param name="unmeasured">Why the script could not be measured, null when it was, see GetSize.Of.</param>
        public static long Of(IMyGridProgram program, long limit, out long time, out string? unmeasured) =>
            GetSize.Of(program, program.GetType().Assembly, limit, out time, out unmeasured);
    }
}
