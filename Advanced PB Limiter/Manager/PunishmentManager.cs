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
    internal static class PunishmentManager
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        internal enum PunishReason
        {
            SingleRuntimeOverLimit,
            AverageRuntimeOverLimit,
            CombinedRuntimeOverLimit,
            CombinedAverageRuntimeOverLimit
        }
        
        internal static async Task CheckForPunishment(TrackedPlayer player, TrackedPBBlock trackedPbBlock, double lastRunTimeMS)
        {
            if (!Config.Enabled) return;
            
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
        
        internal static Task PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, Punishment? punishment = null)
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
                            string message1 = CreateUserMessage(player, trackedPbBlock, reason);
                            if (message1.Length > 0) Chat.Send(message1, player.SteamId, Color.Red);
                            Log.Info($"Turning off {player.PlayerName} for excessive pb run time on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName}");
                            trackedPbBlock.ProgrammableBlock.Enabled = false;
                            break;
                
                        case Punishment.Destroy:
                            string message2 = CreateUserMessage(player, trackedPbBlock, reason);
                            if (message2.Length > 0) Chat.Send(message2, player.SteamId, Color.Red);
                            Log.Info($"Destroying {player.PlayerName} for excessive pb run time on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName}");
                            float damageAmount = trackedPbBlock.ProgrammableBlock.SlimBlock.Integrity - trackedPbBlock.ProgrammableBlock.SlimBlock.BuildIntegrity;
                            trackedPbBlock.ProgrammableBlock.SlimBlock.DecreaseMountLevel(damageAmount, null);
                            break;
                
                        case Punishment.Damage:
                            string message3 = CreateUserMessage(player, trackedPbBlock, reason);
                            if (message3.Length > 0) Chat.Send(message3, player.SteamId, Color.Red);
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
            if (player.SteamId == 0 || MySession.Static.Players.IdentityIsNpc(player.PlayerId) || !MySession.Static.Players.IsPlayerOnline(player.PlayerId))
            {
                Log.Warn($"No punishment message was sent: SteamID[{player.SteamId}]  IsNPC[{MySession.Static.Players.IdentityIsNpc(player.PlayerId)}]  IsOnline[{MySession.Static.Players.IsPlayerOnline(player.PlayerId)}]  GridName[{pbBlock.GridName}]");
                return "";
            }
            
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