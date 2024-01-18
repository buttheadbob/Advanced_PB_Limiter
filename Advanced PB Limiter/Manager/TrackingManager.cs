using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Timers;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage.Game.VisualScripting.Utils;

namespace Advanced_PB_Limiter.Manager
{
    public static class TrackingManager
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static ConcurrentDictionary<long,TrackedPlayer> PlayersTracked { get; } = new ();
        private static readonly Timer _cleanupTimer = new ();
        private static readonly int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Init()
        {
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPlayers();
            if (Config.RemovePlayersWithNoPBFrequencyInMinutes <= 0) return;
            _cleanupTimer.Interval = TimeSpan.FromMinutes(1).TotalMilliseconds;
            _cleanupTimer.Start();
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
    }
}