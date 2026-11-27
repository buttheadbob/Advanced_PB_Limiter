#nullable enable
using System;
using Advanced_PB_Limiter.QuasarContracts;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// A block's last <see cref="Size"/> run times and what the limits read from them. At most <see cref="Spikes"/> of any
    /// <see cref="Size"/> runs are forgiven as spikes, a hitch of the server rather than the script; more than that is a
    /// pattern and counts. Whether a run is a spike is decided once, as it is added, and kept with it: the punishment check
    /// and the combined run time read that decision, and so do the block's average and the reports' marks below 10 runs.
    /// From 10 runs those leave out the 2 slowest runs instead, which need not be the spikes. Not thread-safe: the caller
    /// locks. Nothing allocates after construction but the report copies, as it runs after every run of every script.
    /// </summary>
    public sealed class RunTimeWindow
    {
        public const int Size = 50;

        /// <summary>How many of any <see cref="Size"/> runs may be spikes, and how many slowest runs the average leaves out from 10 runs.</summary>
        public const int Spikes = 2;

        /// <summary>
        /// With no run-time limit, how many times the window's 3rd slowest run (the slowest the average keeps) a run must
        /// take to stand out as a spike. Measured on steady, jittery, heavy-tailed and stepped run times: ordinary variation
        /// stays under it, a hitch of the server is far over it.
        /// </summary>
        public const double StandOut = 3.0;

        // From this many runs the average leaves out the slowest runs, and with no run-time limit a run can be a spike: with
        // fewer there are too few to tell a spike from the script's usual time.
        private const int TrimFrom = 10;

        // In run order, oldest at _next once full, and again kept sorted, so the slowest runs and the count over a limit are
        // read without sorting a copy on every run.
        private readonly double[] _runs = new double[Size];
        private readonly double[] _sorted = new double[Size];
        private int _count;
        private int _next;

        // What became of each run, by its place in _runs, and how many of the window's runs are spikes. _last is the place
        // of the run added last, or -1 after a clear, for MarkLast.
        private readonly RunOutcome[] _outcome = new RunOutcome[Size];
        private int _spikeCount;
        private int _last = -1;

        /// <summary>How many runs the window holds, up to <see cref="Size"/>.</summary>
        public int Count => _count;

        /// <summary>The mean of the window, leaving out its <see cref="Spikes"/> slowest runs once it holds 10 or more. 0 when empty.</summary>
        public double Average { get; private set; }

        /// <summary>
        /// The block's average, which the limits judge it by: <see cref="Average"/> from 10 runs, and below that the mean of
        /// the runs that were not spikes. 0 when empty.
        /// </summary>
        public double AverageWithoutSpikes { get; private set; }

        /// <summary>
        /// Adds a run and decides whether it is a spike, which it then keeps. Only while fewer than <see cref="Spikes"/> of the
        /// window's runs are spikes, so never more than that in any <see cref="Size"/> runs, whatever the limit did meanwhile;
        /// and then, with a run-time limit, a run over it that is one of at most <see cref="Spikes"/> such runs in the window
        /// (<see cref="IsSpikeOver"/>); with none (0), from 10 runs, a run over <see cref="StandOut"/> times the window's 3rd
        /// slowest run. A run time that rises step by step, or varies widely, stays under that, so it takes no spike place
        /// until a run stands out. The run is kept as <see cref="RunOutcome.Forgiven"/> or <see cref="RunOutcome.StoodOut"/>
        /// when a spike, as <see cref="RunOutcome.NotChecked"/> when over the limit otherwise, until the punishment check
        /// marks it counted, and as <see cref="RunOutcome.Under"/> else.
        /// </summary>
        public bool Add(double ms, double runTimeLimit)
        {
            if (_count == Size)
            {
                RemoveSorted(_runs[_next]);
                if (IsSpike(_outcome[_next])) _spikeCount--;
            }
            else
                _count++;

            InsertSorted(ms, _count - 1);
            int slot = _next;
            _runs[slot] = ms;
            _next = (_next + 1) % Size;

            int kept = _count >= TrimFrom ? _count - Spikes : _count;
            double sum = 0;
            for (int index = 0; index < kept; index++)
                sum += _sorted[index];
            Average = kept > 0 ? sum / kept : 0;

            bool spike = _spikeCount < Spikes && (runTimeLimit > 0
                ? ms > runTimeLimit && IsSpikeOver(runTimeLimit)
                : _count >= TrimFrom && ms > StandOut * _sorted[_count - Spikes - 1]);
            RunOutcome outcome = RunOutcome.Under;
            if (spike)
                outcome = runTimeLimit > 0 ? RunOutcome.Forgiven : RunOutcome.StoodOut;
            else if (runTimeLimit > 0 && ms > runTimeLimit)
                outcome = RunOutcome.NotChecked;
            _outcome[slot] = outcome;
            _last = slot;
            if (spike) _spikeCount++;

            UpdateAverageWithoutSpikes();
            return spike;
        }

        /// <summary>
        /// Marks the run added last as <see cref="RunOutcome.Counted"/> (it made an offence) or
        /// <see cref="RunOutcome.InstantKilled"/>. A spike that was the instant kill is no longer one, so it gives back its
        /// spike place and counts in the average again. Nothing after a clear.
        /// </summary>
        public void MarkLast(RunOutcome outcome)
        {
            if (_last < 0) return;
            if (IsSpike(_outcome[_last])) _spikeCount--;
            _outcome[_last] = outcome;
            UpdateAverageWithoutSpikes();
        }

        /// <summary>How many runs of the window took longer than <paramref name="ms"/>.</summary>
        public int CountAbove(double ms)
        {
            int index = Array.BinarySearch(_sorted, 0, _count, ms);
            if (index < 0)
                index = ~index;
            else
                while (index < _count && _sorted[index] <= ms) index++;
            return _count - index;
        }

        /// <summary>
        /// True when at most <see cref="Spikes"/> runs of the window are over <paramref name="limit"/>: a run over it is then a
        /// spike rather than a pattern, see <see cref="Add(double, double)"/>.
        /// </summary>
        public bool IsSpikeOver(double limit) => CountAbove(limit) <= Spikes;

        /// <summary>
        /// Which runs, oldest first, the block's average leaves out: the <see cref="Spikes"/> slowest from 10 runs, and the
        /// runs that were spikes below that, as <see cref="AverageWithoutSpikes"/>. For reports, which may allocate.
        /// </summary>
        public bool[] CopyLeftOutInRunOrder()
        {
            bool[] leftOut = new bool[_count];
            int first = _count == Size ? _next : 0;
            if (_count < TrimFrom)
            {
                for (int index = 0; index < _count; index++)
                    leftOut[index] = IsSpike(_outcome[(first + index) % Size]);
                return leftOut;
            }

            for (int spike = 0; spike < Spikes; spike++)
            {
                int slowest = -1;
                for (int index = 0; index < _count; index++)
                {
                    if (!leftOut[index] && (slowest < 0 || _runs[(first + index) % Size] > _runs[(first + slowest) % Size]))
                        slowest = index;
                }
                leftOut[slowest] = true;
            }
            return leftOut;
        }

        /// <summary>What became of each run, oldest first. For reports, which may allocate.</summary>
        public RunOutcome[] CopyOutcomesInRunOrder()
        {
            RunOutcome[] outcomes = new RunOutcome[_count];
            int first = _count == Size ? _next : 0;
            for (int index = 0; index < _count; index++)
                outcomes[index] = _outcome[(first + index) % Size];
            return outcomes;
        }

        /// <summary>A copy of the runs, oldest first.</summary>
        public double[] CopyInRunOrder()
        {
            double[] copy = new double[_count];
            int first = _count == Size ? _next : 0;
            for (int index = 0; index < _count; index++)
                copy[index] = _runs[(first + index) % Size];
            return copy;
        }

        public void Clear()
        {
            _count = 0;
            _next = 0;
            Average = 0;
            AverageWithoutSpikes = 0;
            Array.Clear(_outcome, 0, Size);
            _spikeCount = 0;
            _last = -1;
        }

        private static bool IsSpike(RunOutcome outcome) => outcome is RunOutcome.Forgiven or RunOutcome.StoodOut;

        // From 10 runs the trimmed mean; below that, when the window has not wrapped and its runs are in places 0 to
        // _count - 1, the mean of the runs that are not spikes.
        private void UpdateAverageWithoutSpikes()
        {
            if (_count >= TrimFrom)
            {
                AverageWithoutSpikes = Average;
                return;
            }
            double sum = 0;
            int kept = 0;
            for (int index = 0; index < _count; index++)
            {
                if (IsSpike(_outcome[index])) continue;
                sum += _runs[index];
                kept++;
            }
            AverageWithoutSpikes = kept > 0 ? sum / kept : 0;
        }

        private void RemoveSorted(double value)
        {
            int index = Array.BinarySearch(_sorted, 0, _count, value);
            Array.Copy(_sorted, index + 1, _sorted, index, _count - index - 1);
        }

        private void InsertSorted(double value, int length)
        {
            int index = Array.BinarySearch(_sorted, 0, length, value);
            if (index < 0)
                index = ~index;
            Array.Copy(_sorted, index, _sorted, index + 1, length - index);
            _sorted[index] = value;
        }
    }

    /// <summary>
    /// Counts a block's runs over the instant kill run time: the instant kill comes on the run that makes the needed number
    /// within the window, in seconds from the first, so a single hitch of the server does not kill a script. Not
    /// thread-safe: the caller locks.
    /// </summary>
    public sealed class InstantKillStrikes
    {
        private int _strikes;
        private long _firstAt;

        /// <summary>
        /// Counts a run over the threshold at <paramref name="now"/> (timestamps of <paramref name="frequency"/> per second).
        /// True when it is the one that kills; the count then starts again.
        /// </summary>
        public bool Strike(long now, long frequency, int needed, int windowSeconds)
        {
            if (needed <= 1)
            {
                _strikes = 0;
                return true;
            }

            // A window under a second, from a hand-edited config, would never let two strikes meet: it is one second at least.
            if (_strikes == 0 || now - _firstAt > Math.Max(1, windowSeconds) * frequency)
            {
                _strikes = 0;
                _firstAt = now;
            }

            if (++_strikes < needed) return false;
            _strikes = 0;
            return true;
        }

        /// <summary>Strikes counted towards the next instant kill, within the current window.</summary>
        public int Count => _strikes;

        public void Clear() => _strikes = 0;
    }
}
