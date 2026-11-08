using System;
using System.Diagnostics;
using System.Globalization;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    public static class HelperUtils
    {
        public static string FormatBytesToKB(long bytes)
        {
            float kilobytes = bytes / 1024f;
            string formattedKilobytes = kilobytes.ToString("N2", CultureInfo.InvariantCulture);

            return $"{formattedKilobytes} KB";
        }
    }
}

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
        /// <param name="startTicks">First point in time.</param>
        /// <param name="endTicks">Last point in time.</param>
        /// <returns>TimeSpan of the range between the two points given.</returns>
        public static TimeSpan TimeElapsed(this long endTicks, long startTicks)
        {
            return TimeSpan.FromSeconds((double)(endTicks - startTicks) / Stopwatch.Frequency);
        }

        /// <summary>
        /// A block's name, from any thread. The game edits it in place on its own thread, so a script renaming its block
        /// can make the read throw. Rare, and the next read has the name again.
        /// </summary>
        public static string ReadName(this MyProgrammableBlock block)
        {
            try
            {
                return block.CustomName.ToString();
            }
            catch (ArgumentOutOfRangeException)
            {
                return block.EntityId.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}