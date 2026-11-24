#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using ExtensionMethods;
using Advanced_PB_Limiter.Manager;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// What is known about one programmable block. Updated on the game thread, once for every run of the script,
    /// so that path must not allocate. The few live values reports need (the names and whether the block is working)
    /// are cached here by the game thread, so reports read the block from any thread without touching game objects;
    /// the sample array is read under its lock, so a recompile that clears it cannot be seen half done.
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

        // The live values reports need, cached on the game thread: at tracking start, and by the cleanup pass that
        // walks every tracked and resting block. Volatile: reports read them from other threads.
        private string _gridName;
        private string _blockName;
        private bool _working;
        private bool _enabled;

        public MyProgrammableBlock ProgrammableBlock { get; }

        /// <summary>The block's entity id, fixed for its life, so reports can key their rows from any thread.</summary>
        public long EntityId { get; }

        /// <summary>The grid's name, safe to write out (SafeNames.SafeName), as the game thread last cached it.</summary>
        public string GridName => Volatile.Read(ref _gridName);

        /// <summary>The block's name, safe to write out, as the game thread last cached it.</summary>
        public string BlockName => Volatile.Read(ref _blockName);

        /// <summary>The grid's name now, or the cached one when it has none. Game thread only.</summary>
        public string CurrentGridName => ProgrammableBlock.CubeGrid is null ? GridName : ProgrammableBlock.ReadGridName();

        /// <summary>The block's Enabled flag, as the game thread last cached it. Any thread.</summary>
        public bool Enabled => Volatile.Read(ref _enabled);

        /// <summary>
        /// Whether the block was working when the game thread last cached it: reports count run times with this,
        /// never the live <see cref="IsRunning"/>. Any thread.
        /// </summary>
        public bool IsRunningCached => Volatile.Read(ref _working);

        public ConcurrentQueue<long> Offences { get; } = new();
        public int Recompiles { get; private set; }

        // Written under _samplesLock, read by reports on other threads without taking it. A double read or write is
        // atomic on the x64 runtime the plugin ships on; Volatile keeps the read from being cached in a register.
        private double _lastRunTimeMS;
        private double _peekRunTimeMS;
        private double _reportAverage;

        public double LastRunTimeMS => Volatile.Read(ref _lastRunTimeMS);
        public double PeekRunTimeMS => Volatile.Read(ref _peekRunTimeMS);
        public long memoryUsage { get; private set; }
        public long LastOffenceTick { get; set; }
        public long LastUpdateTick { get; set; }

        /// <summary>The batch of queued runs in which the block was last instant-killed. Game thread.</summary>
        public BatchMark InstantKill { get; } = new();
        private long PBStartTime { get; set; }

        public TrackedPBBlock(MyProgrammableBlock pbBlock)
        {
            ProgrammableBlock = pbBlock;
            EntityId = pbBlock.EntityId;
            _gridName = string.Empty;
            _blockName = string.Empty;
            PBStartTime = Stopwatch.GetTimestamp();
            LastUpdateTick = PBStartTime;
            RefreshLiveState();
        }

        /// <summary>
        /// Reads the block's names and state, which only the game thread may do, and caches them for reports on other
        /// threads. Called when tracking starts and by the cleanup pass, so a report shows values up to that age.
        /// Game thread only.
        /// </summary>
        public void RefreshLiveState()
        {
            Volatile.Write(ref _gridName, ProgrammableBlock.ReadGridName());
            Volatile.Write(ref _blockName, ProgrammableBlock.ReadName());
            RefreshEnabledState();
        }

        /// <summary>
        /// Reads only the cheap enabled/working flags. The cleanup pass uses this for resting blocks, whose names can stay
        /// from their last run until they run again, while reports still see an accurate on/off state. Game thread only.
        /// </summary>
        public void RefreshEnabledState()
        {
            Volatile.Write(ref _enabled, ProgrammableBlock.Enabled);
            Volatile.Write(ref _working, ProgrammableBlock.IsWorking);
        }

        // Counts the resets of memoryUsage, so a memory pass that read the script before a reset does not write its old
        // size back after it. Changed under _memoryLock.
        private int _memoryResets;
        private readonly object _memoryLock = new();

        /// <summary>To hand to <see cref="updateMemoryUsage"/> by a memory pass, taken before it reads the script.</summary>
        public int MemoryResets => Volatile.Read(ref _memoryResets);

        private int _memoryScanFailed;

        /// <summary>Notes that the memory check could not measure this block; true the first time, so it is logged once.</summary>
        public bool MemoryScanFailed() => Interlocked.Exchange(ref _memoryScanFailed, 1) == 0;

        /// <summary>The memory check could not measure the script, see GetSize.Of: memoryUsage is then a stand-in, not a size.</summary>
        public bool MemoryUnmeasured { get; private set; }

        /// <summary>Records a size measured since <paramref name="resets"/> was read, unless the figure was reset meanwhile.</summary>
        public void updateMemoryUsage(long bytes, int resets, bool unmeasured)
        {
            lock (_memoryLock)
            {
                if (_memoryResets != resets) return;
                memoryUsage = bytes;
                MemoryUnmeasured = unmeasured;
            }
        }

        public void ResetMemoryUsage()
        {
            lock (_memoryLock)
            {
                _memoryResets++;
                memoryUsage = 0;
                MemoryUnmeasured = false;
            }
        }

        // Held while the samples change: a recompile clears them from whichever thread compiles the script, while runs add
        // to them on the game thread. Uncontended in practice, a recompile is rare.
        private readonly object _samplesLock = new();

        public void AddRuntimeData(double lastRunTimeMS)
        {
            lock (_samplesLock)
                AddSample(lastRunTimeMS);
        }

        private void AddSample(double lastRunTimeMS)
        {
            if (lastRunTimeMS > Volatile.Read(ref _peekRunTimeMS))
                Volatile.Write(ref _peekRunTimeMS, lastRunTimeMS);
            Volatile.Write(ref _lastRunTimeMS, lastRunTimeMS);

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
            Volatile.Write(ref _reportAverage, _filteredAverage);
        }

        public bool IsOutlier(double value)
        {
            lock (_samplesLock)
            {
                if (_sampleCount < MAX_SAMPLES) return false;
                return value > _upperFence;
            }
        }

        /// <summary>A copy of the run times, oldest first.</summary>
        public double[] GetRunTimesMS
        {
            get
            {
                lock (_samplesLock)
                {
                    int count = _sampleCount;
                    double[] runTimes = new double[count];
                    int first = count == MAX_SAMPLES ? _nextSample : 0;
                    for (int index = 0; index < count; index++)
                        runTimes[index] = _samples[(first + index) % MAX_SAMPLES];
                    return runTimes;
                }
            }
        }

        /// <summary>
        /// The filtered average of the last 50 runs, or 0 until there are 50. Updated under the sample lock and read
        /// without it, so a report never waits on a run or a recompile.
        /// </summary>
        public double RunTimeMSAvg => Volatile.Read(ref _reportAverage);

        public bool IsUnderGracePeriod(PrivilegedPlayer? privilegedPlayer)
        {
            // 0 uses the server's setting, as in the other per-player fields. Resolved here and not when the row is read,
            // so the 0 stays in the row: CreatePrivilegedUser writes the looked-up player back, and a profile row then
            // uses the setting of the server it is applied to.
            double allowanceSeconds = privilegedPlayer is { StartupAllowance: > 0 } ? privilegedPlayer.StartupAllowance : Config.StartupAllowance;
            return Stopwatch.GetTimestamp() - PBStartTime < allowanceSeconds * Stopwatch.Frequency;
        }

        // The game tick of its last run, and of the last time the limiter or an admin turned it off; -1 before either.
        private long _lastRunGameTick = -1;
        private long _stoppedGameTick = -1;

        /// <summary>Notes a run at the current game tick. Game thread.</summary>
        public void MarkRun() => Interlocked.Exchange(ref _lastRunGameTick, TrackingManager.CurrentTick);

        /// <summary>Notes that the limiter or an admin turned it off now. Game thread.</summary>
        public void MarkStopped()
        {
            Volatile.Write(ref _working, false);
            Volatile.Write(ref _enabled, false);
            Interlocked.Exchange(ref _stoppedGameTick, TrackingManager.CurrentTick);
        }

        /// <summary>
        /// It ran since the last combined check, counted in game ticks as the check is, and was not turned off by the limiter
        /// or an admin after that run. Any thread.
        /// </summary>
        public bool RanSinceCombinedCheck
        {
            get
            {
                long lastRun = Interlocked.Read(ref _lastRunGameTick);
                return lastRun >= 0
                       && TrackingManager.CurrentTick - lastRun <= TrackingManager.COMBINED_CHECK_TICKS
                       && Interlocked.Read(ref _stoppedGameTick) < lastRun;
            }
        }

        /// <summary>
        /// False once the block cannot run: turned off, damaged, unpowered or gone. Game thread only, for the limits;
        /// reports use <see cref="IsRunningCached"/>.
        /// </summary>
        public bool IsRunning => !ProgrammableBlock.Closed && !ProgrammableBlock.MarkedForClose && ProgrammableBlock.IsWorking;

        /// <summary>The last offence or punishment was too recent for another one.</summary>
        public bool IsInGraceAfterOffence => LastOffenceTick != 0 && LastOffenceTick.TicksTillNow_TimeSpan().TotalSeconds <= Config.GraceAfterOffence;

        /// <summary>Counts a recompile, whether or not it clears the history. From whichever thread compiles the script.</summary>
        public void CountRecompile()
        {
            lock (_samplesLock)
                Recompiles++;
        }

        public void ClearRunTimes()
        {
            lock (_samplesLock)
                ClearSamples();
        }

        private void ClearSamples()
        {
            _sampleCount = 0;
            _nextSample = 0;
            _filteredAverage = 0;
            _upperFence = 0;
            Volatile.Write(ref _lastRunTimeMS, 0);
            Volatile.Write(ref _peekRunTimeMS, 0);
            Volatile.Write(ref _reportAverage, 0);
            while (Offences.TryDequeue(out _))
            {
                // ConcurrentQueue has no Clear on .NET Framework, which Legacy and Torch run on.
            }
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
