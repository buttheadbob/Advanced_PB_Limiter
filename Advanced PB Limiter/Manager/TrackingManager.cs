using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage.Game.VisualScripting.Utils;

namespace Advanced_PB_Limiter.Manager
{
    internal static class TrackingManager
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static ConcurrentDictionary<long,TrackedPlayer> PlayersTracked { get; } = new ();
        private static readonly Timer _cleanupTimer = new ();
        private static readonly int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;

        internal static void Init()
        {
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPlayers();
            if (Config.RemovePlayersWithNoPBFrequencyInMinutes > 0)
            {
                _cleanupTimer.Interval = Config.RemovePlayersWithNoPBFrequencyInMinutes * 60 * 1000;
                _cleanupTimer.Start();
                Task.Run(CheckAllUserBlocksForCombinedLimits);
            }
        }
        
        internal static List<TrackedPlayer> GetTrackedPlayerData()
        {
            return PlayersTracked.Values.ToList();
        }

        private static void CleanUpOldPlayers()
        {
            if (!Config.Enabled) return;
            
            if (_lastKnownCleanupInterval != Config.RemovePlayersWithNoPBFrequencyInMinutes)
            {
                if (_lastKnownCleanupInterval == 0)
                {
                    _cleanupTimer.Stop();
                    return;
                }
                _cleanupTimer.Interval = Config.RemovePlayersWithNoPBFrequencyInMinutes * 60 * 1000;
            }
            
            for (int index = PlayersTracked.Count - 1; index >= 0; index--)
            {
                if (PlayersTracked[index].PBBlockCount > 0) continue;
                RemovePlayer(PlayersTracked[index].PlayerId);
            }
        }

        internal static void UpdateTrackingData(MyProgrammableBlock pb, double runTime)
        {
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
            
            player = new TrackedPlayer(pb.CubeGrid.DisplayName, pb.OwnerId);
            player.UpdatePBBlockData(pb, runTime);
            PlayersTracked.TryAdd(pb.OwnerId, player);
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
        
        internal static void PBRecompiled(MyProgrammableBlock pb)
        {
            if (!Config.Enabled) return;
            
            if (PlayersTracked.TryGetValue(pb.OwnerId, out TrackedPlayer? player))
            {
                if (Config.ClearHistoryOnRecompile)
                    player.ResetPBBlockData(pb);
            }
        }
         
        private static void CheckAllUserBlocksForCombinedLimits()
        {
            if (!Config.Enabled) return;
            
            if (!Config.CheckAllUserBlocksCombined) return;
            
            foreach(KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
            {
                if (Config.PrivilegedPlayers.TryGetValue(trackedPlayer.Value.SteamId, out PrivilegedPlayer? privilegedPlayer))
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