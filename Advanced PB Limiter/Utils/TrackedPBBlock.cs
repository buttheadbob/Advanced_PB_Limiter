using System.Collections.Concurrent;
using System.Diagnostics;
using Advanced_PB_Limiter.Settings;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    public sealed class TrackedPBBlock
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance.Config;
        public MyProgrammableBlock? ProgrammableBlock { get; set; }
        public ConcurrentQueue<double> RunTimesMS { get; set; } = new ();
        public string? GridName { get; set; }
        public double PBStartTime { get; set; }
        public bool GracePeriodFinished { get; set; }
        public bool IsRecompiled { get; set; }
        public int Offences { get; set; }
        public int Recompiles { get; set; }

        public TrackedPBBlock(string? gridName)
        {
            GridName = gridName;
            PBStartTime = Stopwatch.GetTimestamp();
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
                    
                    if (count >= Config.MaxPreviousRunsToCalculateMaxAvg)
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
        
        public bool IsUnderGracePeriod()
        {
            return (Stopwatch.GetTimestamp() - PBStartTime) / Stopwatch.Frequency < Config.GracePeriodSeconds;
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

            PBStartTime = 0;
            Offences = 0;
            Recompiles++;
            GracePeriodFinished = false;
        }
    }
}