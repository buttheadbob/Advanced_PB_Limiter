using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using NLog.Fluent;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.VisualScripting.Utils;
using VRage.ModAPI;
using Timer = System.Timers.Timer;

namespace Advanced_PB_Limiter.Manager
{
    public static class TrackingManager
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static ConcurrentDictionary<long,TrackedPlayer> PlayersTracked { get; } = new ();
        private static readonly Timer _cleanupTimer = new ();
        private static readonly int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static ConcurrentDictionary<long, ProgramInfo> ProgrammablePrograms = new();
        private static readonly Timer _memoryCheckTimer = new (5000);

        public static void Init()
        {
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPlayers();
            if (Config.RemovePlayersWithNoPBFrequencyInMinutes <= 0) return;
            _cleanupTimer.Interval = TimeSpan.FromMinutes(1).TotalMilliseconds;
            _cleanupTimer.Start();
            _memoryCheckTimer.Elapsed += (sender, args) => CheckInstanceMemoryUsages();
            _memoryCheckTimer.Start();
        }
        
        public static List<TrackedPlayer> GetTrackedPlayerData()
        {
            return PlayersTracked.Values.ToList();
        }
        
        public static TrackedPlayer? GetTrackedPlayerDataById(long playerId)
        {
            return PlayersTracked.TryGetValue(playerId, out TrackedPlayer? player) 
                ? player 
                : null;
        }

        private static void CleanUpOldPlayers()
        {
            if (!Config.Enabled) return;
            // Get players
            long[] trackedPlayers = PlayersTracked.Keys.ToArray();
            if (trackedPlayers.Length <= 0) return;
            
            for (int index = trackedPlayers.Length - 1; index >= 0; index--)
            {
                if (PlayersTracked[trackedPlayers[index]].PBBlockCount > 0) continue;
                if ((Stopwatch.GetTimestamp() - PlayersTracked[trackedPlayers[index]].LastDataUpdateTick) / Stopwatch.Frequency / 60 < Config.RemovePlayersWithNoPBFrequencyInMinutes) continue;
                RemovePlayer(PlayersTracked[trackedPlayers[index]].PlayerId);
            }
        }

        public static void UpdateTrackingData(MyProgrammableBlock pb, double runTime)
        {
            if (pb.OwnerId == 0)
            {
                if (Config.TurnOffUnownedBlocks) pb.Enabled = false;
                return;
            } // Track un-owned blocks? something to think about...
        
            if (!Config.Enabled) return;
            if (Config.IgnoreNPCs)
            {
                if (MySession.Static.Players.IdentityIsNpc(pb.OwnerId)) return;
            }

            if (runTime < 0)
                runTime = 0;
            
            if (PlayersTracked.TryGetValue(pb.OwnerId, out TrackedPlayer? player))
            {
                player.UpdatePBBlockData(pb, runTime);
                return;
            }
            
            player = new TrackedPlayer(pb.OwnerId);
            PlayersTracked.TryAdd(pb.OwnerId, player);
            player.UpdatePBBlockData(pb, runTime);
        }
        
        private static void RemovePlayer(long Id)
        {
            for (int index = PlayersTracked.Count - 1; index >= 0; index--)
            {
                if (PlayersTracked[index].PlayerId != Id) continue;
                PlayersTracked.Remove(Id);
                break;
            }
        }
        
        public static void PBRecompiled(MyProgrammableBlock? pb)
        {
            if (!Config.Enabled) return;
            if (pb is null) return;

            if (!PlayersTracked.TryGetValue(pb.OwnerId, out TrackedPlayer? player)) return;
            
            if (Config.ClearHistoryOnRecompile)
                player.ResetPBBlockData(pb);
        }
         
        private static void CheckAllUserBlocksForCombinedLimits()
        {
            if (!Config.Enabled) return;
            
            if (!Config.CheckAllUserBlocksCombined) return;
            
            foreach(KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
            {
                if (Config.PrivilegedPlayers.TryGetValue(trackedPlayer.Value.SteamId, out PrivilegedPlayer privilegedPlayer))
                    if (!privilegedPlayer.NoCombinedLimits) continue;

                double totalMS = 0;
                double totalMSAvg = 0;
                double CombinedRuntimeAllowance = privilegedPlayer?.CombinedRuntimeAllowance ?? Config.MaxAllBlocksCombinedRunTimeMS;
                double CombinedRuntimeAverageAllowance = privilegedPlayer?.CombinedRuntimeAverageAllowance ?? Config.MaxAllBlocksCombinedRunTimeMSAvg;
                
                ReadOnlySpan<double> AllPbRuntimes = trackedPlayer.Value.GetAllPBBlocksLastRunTimeMS;
                ReadOnlySpan<double> AllPbRuntimesAvg = trackedPlayer.Value.GetAllPBBlocksMSAvg;
                
                for (int index = AllPbRuntimes.Length - 1; index >= 0; index--)
                {
                    totalMS += AllPbRuntimes[index];
                }
                for (int index = AllPbRuntimesAvg.Length - 1; index >= 0; index--)
                {
                    totalMSAvg += AllPbRuntimesAvg[index];
                }
                
                if (totalMS > CombinedRuntimeAllowance)
                {
                    if (Config.PunishAllUserBlocksCombinedOnExcessLimits)
                    {
                        foreach (TrackedPBBlock pbBlock in trackedPlayer.Value.GetAllPBBlocks)
                        {
                            PunishmentManager.PunishPB(trackedPlayer.Value, pbBlock, PunishmentManager.PunishReason.CombinedRuntimeOverLimit);
                        }
                    }
                    else
                    {
                        Random random = new(DateTime.Now.Millisecond);
                        PunishmentManager.PunishPB(trackedPlayer.Value, trackedPlayer.Value.GetAllPBBlocks[random.Next(0,trackedPlayer.Value.GetAllPBBlocks.Count()-1)], PunishmentManager.PunishReason.CombinedRuntimeOverLimit);
                    }
                }
                
                if (totalMSAvg > CombinedRuntimeAverageAllowance)
                {
                    if (Config.PunishAllUserBlocksCombinedOnExcessLimits)
                    {
                        foreach (TrackedPBBlock pbBlock in trackedPlayer.Value.GetAllPBBlocks)
                        {
                            PunishmentManager.PunishPB(trackedPlayer.Value, pbBlock, PunishmentManager.PunishReason.CombinedAverageRuntimeOverLimit);
                        }
                    }
                    else
                    {
                        Random random = new(DateTime.Now.Millisecond);
                        PunishmentManager.PunishPB(trackedPlayer.Value, trackedPlayer.Value.GetAllPBBlocks[random.Next(0,trackedPlayer.Value.GetAllPBBlocks.Count()-1)], PunishmentManager.PunishReason.CombinedAverageRuntimeOverLimit);
                    }
                }
            }
        }

        public static bool IsPBInstanceReferenceValid(long pbEntityID)
        {
            if (!ProgrammablePrograms.TryGetValue(pbEntityID, out ProgramInfo? programmableProgram)) return false;
            if (programmableProgram == null) return false;
            if (!programmableProgram.ProgramReference.TryGetTarget(out IMyGridProgram? program)) return false;
            return program != null;
        }

        public static bool IsMemoryCheckInProgress(long entityID)
        {
            if (!ProgrammablePrograms.TryGetValue(entityID, out ProgramInfo? programmableProgram)) return false;

            return Volatile.Read(ref programmableProgram.IsChecking) == 1;
        }

        public static void AddPBInstanceReference(long entityID, ProgramInfo programInfo)
        {
            ProgrammablePrograms.AddOrUpdate(entityID, programInfo, (l, info) => info);
        }

        public static void RemovePBInstanceReference(long entityId)
        {
            ProgrammablePrograms.TryRemove(entityId, out _);
        }

        private static void CheckInstanceMemoryUsages()
        {
            if (!Config.EnableMemoryMonitoring) return;
            
            foreach (KeyValuePair<long,ProgramInfo> programInfo in ProgrammablePrograms)
            {
                if (Interlocked.CompareExchange(ref programInfo.Value.IsChecking, 1, 0) != 0)
                    continue;

                try
                {
                    if (!programInfo.Value.ProgramReference.TryGetTarget(out IMyGridProgram? program))
                    {
                        RemovePBInstanceReference(programInfo.Key);
                        continue;
                    }

                    if (program == null)
                    {
                        RemovePBInstanceReference(programInfo.Key);
                        continue;
                    }
                
                    GetSize.OfAssembly(program, out long size, out long time);
                    if (Config.DebugReporting)
                        Log.Info($"Programmable Block[{MyAPIGateway.Entities.GetEntityById(programInfo.Key).Name}] Size[{size}] Time[{TimeSpan.FromTicks(time).TotalMilliseconds}ms]");
                
                    programInfo.Value.LastUpdate = DateTime.Now;

                    PlayersTracked.TryGetValue(programInfo.Value.OwnerID, out TrackedPlayer? trackedPlayer);
                    TrackedPBBlock? block = null;
                
                    if (trackedPlayer == null)
                    {
                        foreach (TrackedPlayer player in PlayersTracked.Values)
                        {
                            block = player.GetTrackedPB(programInfo.Key);
                            if (block != null)
                                break;
                        }
                    }
                    else
                    {
                        block = trackedPlayer.GetTrackedPB(programInfo.Key);
                    }
                
                    if (block == null) continue;
                
                    block.updateMemoryUsage(size);
                }
                finally
                {
                    Interlocked.Exchange(ref programInfo.Value.IsChecking, 0);
                }
            }
        }
    }
}
     