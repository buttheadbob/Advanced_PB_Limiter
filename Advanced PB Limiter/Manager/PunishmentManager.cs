using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace Advanced_PB_Limiter.Manager
{
    public static class PunishmentManager
    {
        private static Logger Log = LogManager.GetCurrentClassLogger();
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance.Config;
        private enum PunishReason
        {
            SingleRuntimeOverLimit,
            AverageRuntimeOverLimit
        }
        
        public static async void CheckForPunishment(TrackedPlayer player, TrackedPBBlock trackedPbBlock, double lastRunTimeMS)
        {
            if (!trackedPbBlock.GracePeriodFinished)
            {
                if (trackedPbBlock.IsUnderGracePeriod())
                    return;
                
                trackedPbBlock.GracePeriodFinished = true;
            }

            if (lastRunTimeMS > Config.MaxRunTimeMS)
            {
                trackedPbBlock.Offences++;
                if (trackedPbBlock.Offences <= Config.MaxOffencesBeforePunishment) return;
                
                await PunishPB(player, trackedPbBlock, PunishReason.SingleRuntimeOverLimit);
            }
            
            if (trackedPbBlock.RunTimeMSAvg > Config.MaxRunTimeMSAvg)
            {
                trackedPbBlock.Offences++;
                if (trackedPbBlock.Offences <= Config.MaxOffencesBeforePunishment) return;
                
                await PunishPB(player, trackedPbBlock, PunishReason.AverageRuntimeOverLimit);
            }
        }
        
        private static Task PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason)
        {
            if (Config.GracefulShutDownRequestDelay > 0)
            {
                ulong steamId = MySession.Static.Players.TryGetSteamId(player.PlayerId);
                Chat.Send($"Your programming block on grid <{trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> will be disabled due to excessive run time in {Config.GracefulShutDownRequestDelay} seconds.", steamId, Color.Red);
                Task.Delay(Config.GracefulShutDownRequestDelay * 1000).ContinueWith(_ =>
                {
                    PerformPunish(trackedPbBlock);
                });
                return Task.CompletedTask;
            }
            
            PerformPunish(trackedPbBlock);
            return Task.CompletedTask;
        }

        private static void PerformPunish(TrackedPBBlock trackedPbBlock)
        {
            if (trackedPbBlock.ProgrammableBlock is null)
            {
                Log.Warn("Attempted to punish a programmable block that was null!  Grid: " + trackedPbBlock.GridName);
                return;
            }
            
            switch (Config.Punishment)
            {
                case Enums.Punishment.TurnOff:
                    trackedPbBlock.ProgrammableBlock.Enabled = false;
                    break;
                
                case Enums.Punishment.Destroy:
                    float damageAmount = trackedPbBlock.ProgrammableBlock.SlimBlock.Integrity - trackedPbBlock.ProgrammableBlock.SlimBlock.BuildIntegrity;
                    trackedPbBlock.ProgrammableBlock.SlimBlock.DecreaseMountLevel(damageAmount, null);
                    break;
                
                case Enums.Punishment.Damage:
                    trackedPbBlock.ProgrammableBlock?.SlimBlock.DoDamage(Config.PunishDamageAmount, MyDamageType.Destruction);
                    break;
            }
        }
    }
}