using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Advanced_PB_Limiter.Settings;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace Advanced_PB_Limiter.Utils
{
    internal sealed class TrackedPBBlock
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance.Config;
        internal MyProgrammableBlock? ProgrammableBlock { get; set; }
        private ConcurrentQueue<double> RunTimesMS { get; set; } = new ();
        internal string GridName { get; set; }
        internal double PBStartTime { get; set; }
        internal bool GracePeriodFinished { get; set; }
        internal bool IsRecompiled { get; set; }
        internal int Offences { get; set; }
        internal int Recompiles { get; set; }
        internal double LastRunTimeMS { get; private set; }

        internal TrackedPBBlock(string gridName, double lastRunTimeMs)
        {
            GridName = gridName;
            PBStartTime = Stopwatch.GetTimestamp();
            LastRunTimeMS = lastRunTimeMs;
            RunTimesMS.Enqueue(lastRunTimeMs);
        }
        
        internal void AddRuntimeData(double lastRunTimeMS)
        {
            LastRunTimeMS = lastRunTimeMS;
            RunTimesMS.Enqueue(lastRunTimeMS);
        }
        
        internal List<double> GetRunTimesMS
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
        
        internal double RunTimeMSAvg
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
        
        internal double RunTimeMSMaxPeek
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
        
        internal bool IsUnderGracePeriod(ulong SteamId)
        {
            ulong steamId = MySession.Static.Players.TryGetSteamId(ProgrammableBlock!.SlimBlock.OwnerId);
            if (Config.PrivilegedPlayers.TryGetValue(steamId, out PrivilegedPlayer? privilegedPlayer))
                return (Stopwatch.GetTimestamp() - PBStartTime) / Stopwatch.Frequency < privilegedPlayer.StartupAllowance;
            
            return (Stopwatch.GetTimestamp() - PBStartTime) / Stopwatch.Frequency < Config.GracefulShutDownRequestDelay;
        }
        
        internal void ClearRunTimes()
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
            Offences = 0;
            Recompiles++;
            GracePeriodFinished = false;
        }
    }
}