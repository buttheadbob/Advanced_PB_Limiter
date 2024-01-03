using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Timers;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace Advanced_PB_Limiter.Utils
{
    internal sealed class TrackedPlayer
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        internal string PlayerName { get; private set; }
        internal long PlayerId { get; private set; }
        private ConcurrentDictionary<long, TrackedPBBlock> PBBlocks { get; } = new();
        internal ulong SteamId { get; private set; }
        internal long LastDataUpdateTick { get; set; }
        private readonly Timer _cleanupTimer = new (Config.RemoveInactivePBsAfterSeconds * 1000);
        private int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds * 1000;
        internal int Offences { get; private set; }
        
        internal TrackedPlayer(string playerName, long playerId)
        {
            PlayerName = playerName;
            PlayerId = playerId;
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPBBlocks();
            _cleanupTimer.Start();
            SteamId = MySession.Static.Players.TryGetSteamId(playerId);
        }
        
        internal int PBBlockCount => PBBlocks.Count;
        
        internal async void UpdatePBBlockData(MyProgrammableBlock pbBlock, double lastRunTimeMS)
        {
            LastDataUpdateTick = Stopwatch.GetTimestamp();
            if (!PBBlocks.TryGetValue(pbBlock.EntityId, out TrackedPBBlock? trackedPBBlock))
            {
                trackedPBBlock = new TrackedPBBlock(pbBlock.CubeGrid.DisplayName, lastRunTimeMS);
                PBBlocks.TryAdd(pbBlock.EntityId, trackedPBBlock);
            }

            trackedPBBlock.AddRuntimeData(lastRunTimeMS);
            await PunishmentManager.CheckForPunishment(this, trackedPBBlock, lastRunTimeMS);
        }
        
        internal List<TrackedPBBlock> GetAllPBBlocks
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
        
        internal ReadOnlySpan<double> GetAllPBBlocksLastRunTimeMS
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
        
        internal ReadOnlySpan<double> GetAllPBBlocksMSAvg
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
            for (int index = PBBlocks.Count - 1; index >= 0; index--)
            {
                // Check for null programmable blocks and remove them
                if (PBBlocks[index].ProgrammableBlock is null)
                    PBBlocks.Remove(index);
                
                if (PBBlocks[index].IsUnderGracePeriod(SteamId)) continue;
                
                PBBlocks.Remove(index);
            }

            if (_lastKnownCleanupInterval == Config.RemoveInactivePBsAfterSeconds) return;
            _cleanupTimer.Interval = Config.RemoveInactivePBsAfterSeconds * 1000;
            _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;
        }
        
        internal void ResetPBBlockData(MyProgrammableBlock pbBlock)
        {
            PBBlocks[pbBlock.EntityId].ClearRunTimes();
        }
    }
}