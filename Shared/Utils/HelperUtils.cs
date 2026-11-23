using System;
using System.Diagnostics;
using System.Globalization;
using Sandbox.Game.Entities.Blocks;

namespace ExtensionMethods
{
    public static class MyExtensions
    {
        /// <summary>
        /// Return a timespan range from the Stopwatch.GetTimeStamp() until now.
        /// </summary>
        /// <param name="ticks">A value retrieved from Stopwatch.GetTimeStamp() that is not now.</param>
        /// <returns>TimeSpan of the range between the timestamp given and now.</returns>
        public static TimeSpan TicksTillNow_TimeSpan(this long ticks)
        {
            // Stopwatch ticks are not TimeSpan ticks: the frequency is 10MHz on most Windows machines but 1GHz on Linux.
            return Stopwatch.GetTimestamp().TimeElapsed(ticks);
        }

        /// <summary>
        /// Return a timespan range between to tick values obtained from Stopwatch.GetTimeStamp().
        /// </summary>
        /// <param name="endTicks">Last point in time.</param>
        /// <param name="startTicks">First point in time.</param>
        /// <returns>TimeSpan of the range between the two points given.</returns>
        public static TimeSpan TimeElapsed(this long endTicks, long startTicks)
        {
            return TimeSpan.FromSeconds((double)(endTicks - startTicks) / Stopwatch.Frequency);
        }

        /// <summary>
        /// A block's name, from any thread, safe to write into logs, chat and reports, see <see cref="SafeNames.SafeName"/>. The game
        /// edits it in place on its own thread, so a script renaming its block can make the read throw. Rare, and the next
        /// read has the name again.
        /// </summary>
        public static string ReadName(this MyProgrammableBlock block)
        {
            try
            {
                return block.CustomName.ToString().SafeName();
            }
            catch (ArgumentOutOfRangeException)
            {
                return block.EntityId.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The name of the block's grid as it is now, see <see cref="SafeNames.SafeName"/>.</summary>
        public static string ReadGridName(this MyProgrammableBlock block) => (block.CubeGrid?.DisplayName ?? "").SafeName();
    }
}