using System.Collections.Generic;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage.Game;
using VRageMath;
using static Advanced_PB_Limiter.Utils.Enums;

namespace Advanced_PB_Limiter.Manager
{
    public static class PunishmentManager
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance.Config;
        public enum PunishReason
        {
            SingleRuntimeOverLimit,
            AverageRuntimeOverLimit,
            CombinedRuntimeOverLimit,
            CombinedAverageRuntimeOverLimit
        }
        
        public static async Task CheckForPunishment(TrackedPlayer player, TrackedPBBlock trackedPbBlock, double lastRunTimeMS)
        {
            if (!trackedPbBlock.GracePeriodFinished)
            {
                if (trackedPbBlock.IsUnderGracePeriod(player.SteamId))
                    return;
                
                trackedPbBlock.GracePeriodFinished = true;
            }

            double allowedRunTime = Config.MaxRunTimeMS;
            double allowedRunTimeAvg = Config.MaxRunTimeMSAvg;
            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer? privilegedPlayer))
            {
                allowedRunTime = privilegedPlayer.RuntimeAllowance;
                allowedRunTimeAvg = privilegedPlayer.RuntimeAverageAllowance;
            }
                
            if (lastRunTimeMS > allowedRunTime)
            {
                trackedPbBlock.Offences++;
                if (trackedPbBlock.Offences <= Config.MaxOffencesBeforePunishment) return;
                
                await PunishPB(player, trackedPbBlock, PunishReason.SingleRuntimeOverLimit);
            }
            
            if (trackedPbBlock.RunTimeMSAvg > allowedRunTimeAvg)
            {
                trackedPbBlock.Offences++;
                if (trackedPbBlock.Offences <= Config.MaxOffencesBeforePunishment) return;
                
                await PunishPB(player, trackedPbBlock, PunishReason.AverageRuntimeOverLimit);
            }
        }
        
        public static Task PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, Punishment? punishment = null)
        {
            if (trackedPbBlock.ProgrammableBlock is null)
            {
                Log.Warn("Attempted to punish a programmable block that was null!  Grid: " + trackedPbBlock.GridName);
                return Task.CompletedTask;
            }
            
            int gracePeriodSeconds = Config.GracefulShutDownRequestDelay;
            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer? privilegedPlayer))
                gracePeriodSeconds = privilegedPlayer.GracefulShutDownRequestDelay;
            
            if (gracePeriodSeconds > 0)
            {
                trackedPbBlock.ProgrammableBlock.Run($"GracefulShutDown::{gracePeriodSeconds}", UpdateType.Script);
                Task.Delay(Config.GracefulShutDownRequestDelay * 1000).ContinueWith(_ =>
                {
                    PerformPunish(trackedPbBlock, player, reason, punishment);
                });
                return Task.CompletedTask;
            }
            
            PerformPunish(trackedPbBlock, player, reason, punishment);
            return Task.CompletedTask;
        }

        private static void PerformPunish(TrackedPBBlock trackedPbBlock, TrackedPlayer player, PunishReason reason, Punishment? punishment = null)
        {
            if (trackedPbBlock.ProgrammableBlock is null)
            {
                Log.Warn("Attempted to punish a programmable block that was null!  Grid: " + trackedPbBlock.GridName);
                return;
            }

            switch (punishment)
            {
                case null:
                    switch (Config.Punishment)
                    {
                        case Punishment.TurnOff:
                            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason), player.SteamId, Color.Red);
                            Log.Info($"Turning off {player.PlayerName} for excessive pb run time on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName}");
                            trackedPbBlock.ProgrammableBlock.Enabled = false;
                            break;
                
                        case Punishment.Destroy:
                            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason), player.SteamId, Color.Red);
                            Log.Info($"Destroying {player.PlayerName} for excessive pb run time on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName}");
                            float damageAmount = trackedPbBlock.ProgrammableBlock.SlimBlock.Integrity - trackedPbBlock.ProgrammableBlock.SlimBlock.BuildIntegrity;
                            trackedPbBlock.ProgrammableBlock.SlimBlock.DecreaseMountLevel(damageAmount, null);
                            break;
                
                        case Punishment.Damage:
                            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason), player.SteamId, Color.Red);
                            Log.Info($"Damaging {player.PlayerName} for excessive pb run time on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName}");
                            trackedPbBlock.ProgrammableBlock?.SlimBlock.DoDamage(Config.PunishDamageAmount, MyDamageType.Destruction);
                            break;
                    }
                    break;
                
                case Punishment.TurnOff:
                    trackedPbBlock.ProgrammableBlock.Enabled = false;
                    break;
            }
        }
        
        private static string CreateUserMessage(TrackedPlayer player, TrackedPBBlock pbBlock, PunishReason reason )
        {
            switch (reason)
            {
                case PunishReason.AverageRuntimeOverLimit:
                    return $"Your programming block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> will be punished due to excessive average run time.";
                
                case PunishReason.CombinedAverageRuntimeOverLimit:
                    return $"Your programming block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> will be punished due to excessive combined average run time.";
                
                case PunishReason.SingleRuntimeOverLimit:
                    return $"Your programming block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> will be punished due to excessive run time.";
                
                case PunishReason.CombinedRuntimeOverLimit:
                    return $"Your programming block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> will be punished due to excessive combined run time.";
                
                default:
                    return $"Your programming block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> will be punished for some reason, please check with server admins.";
            }
        }
    }
}