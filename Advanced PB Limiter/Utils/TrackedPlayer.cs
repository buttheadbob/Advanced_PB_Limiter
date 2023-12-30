using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Timers;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils
{
    public sealed class TrackedPlayer
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance.Config;
        public string PlayerName { get; private set; }
        public long PlayerId { get; private set; }
        public Dictionary<long, TrackedPBBlock> PBBlocks { get; private set; } = new();
        public long LastDataUpdateTick { get; set; }
        private readonly Timer _cleanupTimer = new (Config.RemoveInactivePBsAfterSeconds * 1000);
        private int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds * 1000;
        
        public TrackedPlayer(string playerName, long playerId)
        {
            PlayerName = playerName;
            PlayerId = playerId;
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPBBlocks();
            _cleanupTimer.Start();
        }
        
        public int PBBlockCount => PBBlocks.Count;
        
        public void UpdatePBBlockData(MyProgrammableBlock pbBlock, double lastRunTimeMS)
        {
            LastDataUpdateTick = Stopwatch.GetTimestamp();
            if (!PBBlocks.TryGetValue(pbBlock.EntityId, out TrackedPBBlock? trackedPBBlock))
            {
                trackedPBBlock = new TrackedPBBlock(pbBlock.CubeGrid.DisplayName);
                PBBlocks.Add(pbBlock.EntityId, trackedPBBlock);
            }

            trackedPBBlock.RunTimesMS.Enqueue(lastRunTimeMS);
            PunishmentManager.CheckForPunishment(this, trackedPBBlock, lastRunTimeMS);
        }
        
        public List<double> GetAllPBBlocksLastRunTimeMS()
        {
            List<double> total = new ();
            for (int index = PBBlocks.Count - 1; index >= 0; index--)
            {
                for (int temp_i = PBBlocks[index].RunTimesMS.ToList().Count - 1; temp_i >= 0; temp_i--)
                {
                    total.Add(PBBlocks[index].RunTimesMS.ToList()[temp_i]);
                }
            }

            return total;
        }
        
        public List<double> GetAllPBBlocksLastRunTimeMSAvg()
        {
            List<double> total = new ();
            for (int index = PBBlocks.Count - 1; index >= 0; index--)
            {
                total.Add(PBBlocks[index].RunTimeMSAvg);
            }

            return total;
        }
        
        private void CleanUpOldPBBlocks()
        {
            for (int index = PBBlocks.Count - 1; index >= 0; index--)
            {
                // Check for null programmable blocks and remove them
                if (PBBlocks[index].ProgrammableBlock is null)
                    PBBlocks.Remove(index);
                
                if (PBBlocks[index].IsUnderGracePeriod()) continue;
                
                PBBlocks.Remove(index);
            }

            if (_lastKnownCleanupInterval == Config.RemoveInactivePBsAfterSeconds) return;
            _cleanupTimer.Interval = Config.RemoveInactivePBsAfterSeconds * 1000;
            _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;
        }
        
        public void ResetPBBlockData(MyProgrammableBlock pbBlock)
        {
            PBBlocks[pbBlock.EntityId].ClearRunTimes();
        }
    }
}