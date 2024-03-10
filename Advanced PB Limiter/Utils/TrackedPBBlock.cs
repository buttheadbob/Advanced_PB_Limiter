using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Advanced_PB_Limiter.Settings;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    public sealed class TrackedPBBlock
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public MyProgrammableBlock? ProgrammableBlock { get; }
        private ConcurrentQueue<double> RunTimesMS { get; set; } = new ();
        public string GridName { get; }
        private long PBStartTime { get; set; }
        public ConcurrentQueue<long> Offences { get; private set; } = new();
        public int Recompiles { get; private set; }
        public double LastRunTimeMS { get; private set; }
        private double RunTimesSum { get; set; }= 0;
        public double PeekRunTimeMS { get; private set; } = 0;
        
        private long _lastOffenceTick;
        public long LastOffenceTick
        {
            get => _lastOffenceTick;
            set => Interlocked.Exchange(ref _lastOffenceTick, value);
        }
        public bool TieredBlock { get; private set; }
        public int TierLevel { get; private set; }
        
        private long _lastUpdateTick;
        public long LastUpdateTick
        {
            get => Interlocked.Read(ref _lastUpdateTick);
            set => Interlocked.Exchange(ref _lastUpdateTick, value);
        }

        public TrackedPBBlock(string gridName, MyProgrammableBlock pbBlock)
        {
            GridName = gridName;
            PBStartTime = Stopwatch.GetTimestamp();
            ProgrammableBlock = pbBlock;
            LastUpdateTick = Stopwatch.GetTimestamp();
        }

        public void AddRuntimeData(double lastRunTimeMS, ulong steamId)
        {
            if (IsUnderGracePeriod(steamId)) return;
            if (lastRunTimeMS > PeekRunTimeMS)
                PeekRunTimeMS = lastRunTimeMS;
            LastRunTimeMS = lastRunTimeMS;
            if (RunTimesMS.Count >= Config.MaxRunsToTrack)
                if (RunTimesMS.TryDequeue(out double oldRunTimeMS))
                    RunTimesSum -= oldRunTimeMS;
            
            RunTimesMS.Enqueue(lastRunTimeMS);
            RunTimesSum += lastRunTimeMS;
            LastUpdateTick = Stopwatch.GetTimestamp();
        }
        
        public double[] GetRunTimesMS
        {
            get
            {
                double[] runTimes = new double[RunTimesMS.Count];
                int index = 0;

                using IEnumerator<double> enumerator = RunTimesMS.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    runTimes[index++] = enumerator.Current;
                }
                return runTimes;
            }
        }
        
        public double RunTimeMSAvg
        {
            get
            {
                // If the queue doesnt have enough samples to calculate the average, return 0
                if (RunTimesMS.Count < Config.MaxRunsToTrack)
                    return 0;
                
                return RunTimesSum / RunTimesMS.Count;
            }
        }
        
        public double RunTimeMSMaxPeek
        {
            get
            {
                double[] runTimes = GetRunTimesMS;
                double max = 0;
                for (int index = 0; index < runTimes.Length; index++)
                {
                    if (runTimes[index] > max)
                        max = runTimes[index];
                }

                return max;
            }
        }
        
        public bool IsUnderGracePeriod(ulong SteamId)
        {
            if (Config.PrivilegedPlayers.TryGetValue(SteamId, out PrivilegedPlayer privilegedPlayer))
                return (double)(Stopwatch.GetTimestamp() - PBStartTime) / Stopwatch.Frequency < privilegedPlayer.StartupAllowance;
            
            double result = (double)(Stopwatch.GetTimestamp() - PBStartTime) / Stopwatch.Frequency;
            return result < 15;
        }
        
        public void ClearRunTimes()
        {
            RunTimesMS = new ConcurrentQueue<double>();
            LastRunTimeMS = 0;
            RunTimesSum = 0;
            Offences = new ConcurrentQueue<long>();
            Recompiles++;
            PBStartTime = Stopwatch.GetTimestamp();
            _lastUpdateTick = Stopwatch.GetTimestamp();
            PeekRunTimeMS = 0;
        }
    }
}