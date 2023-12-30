using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Timers;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Manager
{
    public static class TrackingManager
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance.Config;
        private static ConcurrentDictionary<long,TrackedPlayer> PlayersTracked { get; set; } = new ();
        private static Timer _cleanupTimer = new ();
        private static int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;

        public static void Init()
        {
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPlayers();
            if (Config.RemovePlayersWithNoPBFrequencyInMinutes > 0)
            {
                _cleanupTimer.Interval = Config.RemovePlayersWithNoPBFrequencyInMinutes * 60 * 1000;
                _cleanupTimer.Start();
            }
        }

        private static void CleanUpOldPlayers()
        {
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

        public static void UpdateTrackingData(MyProgrammableBlock pb, double runTime)
        {
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
        
        public static void PBRecompiled(MyProgrammableBlock pb)
        {
            if (PlayersTracked.TryGetValue(pb.OwnerId, out TrackedPlayer? player))
            {
                if (Config.ClearHistoryOnRecompile)
                    player.ResetPBBlockData(pb);
            }
        }
    }
}