using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Timers;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace Advanced_PB_Limiter.Utils
{
    public sealed class TrackedPlayer
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static Logger Log => LogManager.GetLogger("Advanced PB Limiter - Tracked Player: ");
        public string? PlayerName { get; private set; }
        public long PlayerId { get; private set; }
        private ConcurrentDictionary<long, TrackedPBBlock> PBBlocks { get; } = new();
        public ulong SteamId { get; private set; }
        public long LastDataUpdateTick { get; set; }
        private readonly Timer _cleanupTimer = new (Config.RemoveInactivePBsAfterSeconds * 1000);
        private int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds * 1000;
        public int Offences = 0;
        
        public TrackedPlayer(long playerId)
        {
            PlayerName = MySession.Static.Players.TryGetIdentity(playerId).DisplayName;
            PlayerId = playerId;
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPBBlocks();
            _cleanupTimer.Start();
            SteamId = MySession.Static.Players.TryGetSteamId(playerId);
        }
        
        public int PBBlockCount => PBBlocks.Count;

        public TrackedPBBlock? GetTrackedPB(long entityId)
        {
            return PBBlocks.TryGetValue(entityId, out TrackedPBBlock? trackedPBBlock) 
                ? trackedPBBlock 
                : null;
        }
        
        public void UpdatePBBlockData(MyProgrammableBlock pbBlock, double lastRunTimeMS)
        {
            LastDataUpdateTick = Stopwatch.GetTimestamp();
            if (!PBBlocks.TryGetValue(pbBlock.EntityId, out TrackedPBBlock? trackedPBBlock))
            {
                trackedPBBlock = new TrackedPBBlock(pbBlock.CubeGrid.DisplayName, pbBlock);
                PBBlocks.TryAdd(pbBlock.EntityId, trackedPBBlock);
                return;
            }

            if (trackedPBBlock.IsUnderGracePeriod(MySession.Static.Players.TryGetSteamId(pbBlock.OwnerId)))
                return;
            
            trackedPBBlock.AddRuntimeData(lastRunTimeMS, SteamId);
            PunishmentManager.CheckForPunishment(this, trackedPBBlock.ProgrammableBlock!.EntityId, lastRunTimeMS);
        }
        
        public List<TrackedPBBlock> GetAllPBBlocks
        {
            get
            {
                List<TrackedPBBlock> total = new();
                foreach (TrackedPBBlock pbBlock in PBBlocks.Values)
                {
                    if (pbBlock?.ProgrammableBlock is null) continue;
                    total.Add(pbBlock);
                }

                return total;
            }
        }
        
        public ReadOnlySpan<double> GetAllPBBlocksLastRunTimeMS
        {
            get
            {
                double[] total = new double[PBBlocks.Count];
                for (int index = 0; index < PBBlocks.Count; index++)
                {
                    total[index] = PBBlocks[PBBlocks.Count - 1 - index].LastRunTimeMS;
                }

                return new ReadOnlySpan<double>(total);
            }
        }
        
        public ReadOnlySpan<double> GetAllPBBlocksMSAvg
        {
            get
            {
                double[] total = new double[PBBlocks.Count];
                for (int index = 0; index < PBBlocks.Count; index++)
                {
                    total[index] = PBBlocks[PBBlocks.Count - 1 - index].RunTimeMSAvg;
                }

                return new ReadOnlySpan<double>(total);
            }
        }
        
        private void CleanUpOldPBBlocks()
        {
            List<TrackedPBBlock> toRemove = new();
            foreach (TrackedPBBlock pbBlock in PBBlocks.Values)
            {
                // Check for null programmable blocks and remove them
                if (pbBlock.ProgrammableBlock is null)
                    toRemove.Add(pbBlock);
                
                if ((Stopwatch.GetTimestamp() - pbBlock.LastUpdateTick) / Stopwatch.Frequency > Config.RemoveInactivePBsAfterSeconds)
                    toRemove.Add(pbBlock);
            }

            foreach (TrackedPBBlock block in toRemove)
            {
                PBBlocks.TryRemove(block.ProgrammableBlock!.EntityId, out _);
            }

            // Update cleanup interval if it has changed
            if (_lastKnownCleanupInterval == Config.RemoveInactivePBsAfterSeconds) return;
            _cleanupTimer.Interval = Config.RemoveInactivePBsAfterSeconds * 1000;
            _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;
        }
        
        public void ResetPBBlockData(MyProgrammableBlock pbBlock)
        {
            if (!PBBlocks.TryGetValue(pbBlock.EntityId, out TrackedPBBlock trackedPBBlock))
            {
                // Log.Warn("Unable to find PB Block to reset!");
                // "Check Code" button in the pb interface will cause this to happen, probably compiles it in a fake or temp pb which isnt in the list. 
                return;
            }

            if (trackedPBBlock.ProgrammableBlock is null)
            {
                Log.Warn("PB Block is null to reset!");
                return;
            }
            
            PBBlocks[trackedPBBlock.ProgrammableBlock.EntityId].ClearRunTimes();
            PBBlocks[trackedPBBlock.ProgrammableBlock.EntityId].ResetMemoryUsage();
        }
    }
}