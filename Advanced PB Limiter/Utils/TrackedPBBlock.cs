using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Advanced_PB_Limiter.Settings;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace Advanced_PB_Limiter.Utils
{
    public sealed class TrackedPBBlock
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public MyProgrammableBlock? ProgrammableBlock { get; set; }
        private ConcurrentQueue<double> RunTimesMS { get; set; } = new ();
        public string GridName { get; set; }
        public double PBStartTime { get; set; }
        public bool GracePeriodFinished { get; set; }
        public bool IsRecompiled { get; set; }
        public ConcurrentStack<long> Offences { get; set; } = new();
        public int Recompiles { get; set; }
        public double LastRunTimeMS { get; private set; }
        
        private long _lastOffenceTick;
        public long LastOffenceTick
        {
            get => _lastOffenceTick;
            set => Interlocked.Exchange(ref _lastOffenceTick, value);
        }
        
        private long _lastUpdateTick;
        public long LastUpdateTick
        {
            get => Interlocked.Read(ref _lastUpdateTick);
            set => Interlocked.Exchange(ref _lastUpdateTick, value);
        }

        public TrackedPBBlock(string gridName, double lastRunTimeMs, MyProgrammableBlock pbBlock)
        {
            GridName = gridName;
            PBStartTime = Stopwatch.GetTimestamp();
            LastRunTimeMS = lastRunTimeMs;
            RunTimesMS.Enqueue(lastRunTimeMs);
            ProgrammableBlock = pbBlock;
            LastUpdateTick = Stopwatch.GetTimestamp();
        }
        
        public void AddRuntimeData(double lastRunTimeMS)
        {
            LastRunTimeMS = lastRunTimeMS;
            RunTimesMS.Enqueue(lastRunTimeMS);
            LastUpdateTick = Stopwatch.GetTimestamp();
        }
        
        public List<double> GetRunTimesMS
        {
            get
            {
                List<double> total = new ();
            
                int count = 0;
                foreach (double time in RunTimesMS)
                {
                    count++;
                    total.Add(time);
                    if (count >= Config.MaxRunsToTrack) break;
                }

                return total;
            }
            
        }
        
        public double RunTimeMSAvg
        {
            get
            {
                double total = 0;
                int count = 0;
                foreach (double time in RunTimesMS)
                {
                    count++;
                    total += time;
                    
                    if (count >= Config.MaxRunsToTrack)
                    {
                        return total / RunTimesMS.Count;
                    }
                }

                // If the queue doesnt have enough samples to calculate the average, return 0
                return 0;
            }
        }
        
        public double RunTimeMSMaxPeek
        {
            get
            {
                double total = 0;
                foreach (double time in RunTimesMS)
                {
                    if (time > total)
                    {
                        total = time;
                    }
                }

                return total / RunTimesMS.Count;
            }
        }
        
        public bool IsUnderGracePeriod(ulong SteamId)
        {
            if (Config.PrivilegedPlayers.TryGetValue(SteamId, out PrivilegedPlayer privilegedPlayer))
                return (Stopwatch.GetTimestamp() - PBStartTime) / Stopwatch.Frequency < privilegedPlayer.StartupAllowance;
            
            return (Stopwatch.GetTimestamp() - PBStartTime) / Stopwatch.Frequency < 2;
        }
        
        public void ClearRunTimes()
        {
            // This allows the queue to be cleared without removing any new items that may have been added during the process.
            int initialCount = RunTimesMS.Count;
            for (int i = 0; i < initialCount; i++)
            {
                if (!RunTimesMS.TryDequeue(out _))
                {
                    break; // Break if the queue is empty
                }
            }

            IsRecompiled = true;
            PBStartTime = 0;
            Offences.Clear();
            Recompiles++;
            GracePeriodFinished = false;
        }
    }
}