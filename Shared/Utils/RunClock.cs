#nullable enable
using System;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Taken by the patch around a script's run when it starts, and handed back when it ends, one per run: a script that
    /// runs another through TryRun gets a stamp of its own for each, as the patches keep one per call.
    /// </summary>
    public readonly struct RunStamp
    {
        internal readonly bool Started;
        internal readonly int Collections;
        internal readonly double NestedBefore;
        internal readonly object? Program;
        internal readonly double MeasuredBefore;
        internal readonly bool EndedThisFrameBefore;

        internal RunStamp(int collections, double nestedBefore, object? program, double measuredBefore, bool endedThisFrameBefore)
        {
            Started = true;
            Collections = collections;
            NestedBefore = nestedBefore;
            Program = program;
            MeasuredBefore = measuredBefore;
            EndedThisFrameBefore = endedThisFrameBefore;
        }
    }

    /// <summary>
    /// What a run is charged: the game's own wall time for it, as it always was, less the runs it made of other blocks
    /// through TryRun, which are charged to those blocks; nothing is taken off for anything else, so work a script hands to
    /// another thread and waits for still counts. Also tells whether a garbage collection happened during the run, as the
    /// instant kill leaves such a run to the ordinary limits: a collection can hold every thread still for longer than the
    /// script ran, and nothing on .NET Framework tells that pause apart from the script's own time.
    /// </summary>
    public static class RunClock
    {
        // The wall time of the finished runs on this thread, in ms, so a run can take off the runs nested in it.
        [ThreadStatic]
        private static double _finishedMs;

        /// <param name="program">The block's script instance before this call, or null.</param>
        /// <param name="measuredMs">The game's figure for the block's last run, before this call.</param>
        /// <param name="endedThisFrame">The game's last run of the block ended in this frame, before this call (TimeSinceLastRun is 0).</param>
        public static RunStamp Start(object? program, double measuredMs, bool endedThisFrame) =>
            new(GC.CollectionCount(0), _finishedMs, program, measuredMs, endedThisFrame);

        /// <summary>
        /// Ends the run the stamp was taken for, which the game measured as <paramref name="measuredMs"/>, and returns what
        /// it is charged. Call it for every run that was stamped, in the order they end, so nested runs are accounted for.
        /// </summary>
        /// <param name="programAfter">The block's script instance after this call, or null.</param>
        /// <param name="endedThisFrame">The game's last run of the block ended in this frame, after this call.</param>
        public static double Finish(in RunStamp start, double measuredMs, object? programAfter, bool endedThisFrame, out bool collected)
        {
            collected = false;
            if (!start.Started) return measuredMs;
            collected = GC.CollectionCount(0) != start.Collections;
            double measured = RanMain(start, measuredMs, programAfter, endedThisFrame) ? Math.Max(0, measuredMs) : 0;
            double nested = _finishedMs - start.NestedBefore;
            // The whole run counts as finished time for a run it is nested in.
            _finishedMs = start.NestedBefore + measured;
            return Math.Max(0, measured - nested);
        }

        /// <summary>
        /// Whether the call ran Main() to its end, which is when the game sets a new figure and ends the run in this frame.
        /// Not when the call put a new instance in place: the first step of creating a script's instance goes through the
        /// same call and keeps the old script's last figure, which the game resets only after. Nor when Main() did not
        /// reach its end yet the script was kept, as when a TryRun nested too deep throws or another plugin skips the
        /// call: the figure is then the last run's. A script ended by its exception has its figure reset to 0 either way.
        /// The figure changed, or the run ended in this frame where the last one had not: a run whose figure is exactly
        /// the last one's, as two short runs of the same tick count are, counts unless the block already ran this frame.
        /// </summary>
        private static bool RanMain(in RunStamp start, double measuredMs, object? programAfter, bool endedThisFrame)
        {
            if (!ReferenceEquals(programAfter, start.Program)) return false;
            bool newFigure = BitConverter.DoubleToInt64Bits(measuredMs) != BitConverter.DoubleToInt64Bits(start.MeasuredBefore);
            return newFigure || (endedThisFrame && !start.EndedThisFrameBefore);
        }
    }
}
