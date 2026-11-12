using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using ExtensionMethods;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// What is known about one programmable block. Updated on the game thread, once for every run of the script,
    /// so that path must not allocate. Reports read it from other threads: every value they read is a single
    /// double, long or int, so the worst they can see is a value one run out of date.
    /// </summary>
    public sealed class TrackedPBBlock
    {
        private const int MAX_SAMPLES = 50;
        private static IPbLimiterConfig Config => LimiterCore.Config;

        // The last MAX_SAMPLES run times, twice: in run order, oldest at _nextSample once full, and kept sorted.
        // Keeping them sorted costs one small array move per run, sorting a copy would allocate and sort on every run.
        private readonly double[] _samples = new double[MAX_SAMPLES];
        private readonly double[] _sorted = new double[MAX_SAMPLES];
        private int _sampleCount;
        private int _nextSample;
        private double _filteredAverage;
        private double _upperFence;

        public MyProgrammableBlock ProgrammableBlock { get; }
        public string GridName { get; }
        public ConcurrentQueue<long> Offences { get; } = new();
        public int Recompiles { get; private set; }
        public double LastRunTimeMS { get; private set; }
        public double PeekRunTimeMS { get; private set; }
        public long memoryUsage { get; private set; }
        public long LastOffenceTick { get; set; }
        public long LastUpdateTick { get; set; }
        private long PBStartTime { get; set; }

        /// <summary>The script was told to shut down and the punishment is waiting for the end of its grace period.</summary>
        public bool ShutdownRequested { get; set; }

        public TrackedPBBlock(string gridName, MyProgrammableBlock pbBlock)
        {
            GridName = gridName;
            ProgrammableBlock = pbBlock;
            PBStartTime = Stopwatch.GetTimestamp();
            LastUpdateTick = PBStartTime;
        }

        public void updateMemoryUsage(long bytes)
        {
            memoryUsage = bytes;
        }

        public void ResetMemoryUsage()
        {
            memoryUsage = 0;
        }

        public void AddRuntimeData(double lastRunTimeMS)
        {
            if (lastRunTimeMS > PeekRunTimeMS)
                PeekRunTimeMS = lastRunTimeMS;
            LastRunTimeMS = lastRunTimeMS;

            if (_sampleCount == MAX_SAMPLES)
                RemoveSorted(_samples[_nextSample], MAX_SAMPLES);
            else
                _sampleCount++;

            InsertSorted(lastRunTimeMS, _sampleCount - 1);
            _samples[_nextSample] = lastRunTimeMS;
            _nextSample = (_nextSample + 1) % MAX_SAMPLES;

            if (_sampleCount == MAX_SAMPLES)
                RecomputeFilteredStats();
        }

        private void RemoveSorted(double value, int length)
        {
            int index = Array.BinarySearch(_sorted, 0, length, value);
            Array.Copy(_sorted, index + 1, _sorted, index, length - index - 1);
        }

        private void InsertSorted(double value, int length)
        {
            int index = Array.BinarySearch(_sorted, 0, length, value);
            if (index < 0)
                index = ~index;
            Array.Copy(_sorted, index, _sorted, index + 1, length - index);
            _sorted[index] = value;
        }

        private void RecomputeFilteredStats()
        {
            double q1 = _sorted[MAX_SAMPLES / 4];
            double q3 = _sorted[MAX_SAMPLES * 3 / 4];
            double upperFence = q3 + 1.5 * (q3 - q1);

            double filteredSum = 0;
            int filteredCount = 0;
            while (filteredCount < MAX_SAMPLES && _sorted[filteredCount] <= upperFence)
                filteredSum += _sorted[filteredCount++];

            _upperFence = upperFence;
            _filteredAverage = filteredCount > 0 ? filteredSum / filteredCount : 0;
        }

        public bool IsOutlier(double value)
        {
            if (_sampleCount < MAX_SAMPLES) return false;
            return value > _upperFence;
        }

        /// <summary>A copy of the run times, oldest first.</summary>
        public double[] GetRunTimesMS
        {
            get
            {
                int count = _sampleCount;
                double[] runTimes = new double[count];
                int first = count == MAX_SAMPLES ? _nextSample : 0;
                for (int index = 0; index < count; index++)
                    runTimes[index] = _samples[(first + index) % MAX_SAMPLES];
                return runTimes;
            }
        }

        public double RunTimeMSAvg => _sampleCount < MAX_SAMPLES ? 0 : _filteredAverage;

        public bool IsUnderGracePeriod(PrivilegedPlayer? privilegedPlayer)
        {
            // 0 uses the server's setting, as in the other per-player fields. Resolved here and not when the row is read,
            // so the 0 stays in the row: CreatePrivilegedUser writes the looked-up player back, and a profile row then
            // uses the setting of the server it is applied to.
            double allowanceSeconds = privilegedPlayer is { StartupAllowance: > 0 } ? privilegedPlayer.StartupAllowance : Config.StartupAllowance;
            return Stopwatch.GetTimestamp() - PBStartTime < allowanceSeconds * Stopwatch.Frequency;
        }

        /// <summary>False once the block cannot run: turned off, damaged, unpowered or gone. It stays tracked for a while after that.</summary>
        public bool IsRunning => !ProgrammableBlock.Closed && !ProgrammableBlock.MarkedForClose && ProgrammableBlock.IsWorking;

        /// <summary>The last offence or punishment was too recent for another one.</summary>
        public bool IsInGraceAfterOffence => LastOffenceTick != 0 && LastOffenceTick.TicksTillNow_TimeSpan().TotalSeconds <= Config.GraceAfterOffence;

        public void ClearRunTimes()
        {
            _sampleCount = 0;
            _nextSample = 0;
            _filteredAverage = 0;
            _upperFence = 0;
            LastRunTimeMS = 0;
            PeekRunTimeMS = 0;
            while (Offences.TryDequeue(out _)) { }
            Recompiles++;
            PBStartTime = Stopwatch.GetTimestamp();
            LastUpdateTick = PBStartTime;
        }

        /// <summary>The offences older than this many minutes no longer count.</summary>
        public void ForgetOffencesOlderThan(int minutes)
        {
            while (Offences.TryPeek(out long oldest) && oldest.TicksTillNow_TimeSpan().TotalMinutes > minutes)
                Offences.TryDequeue(out _);
        }
    }
}
