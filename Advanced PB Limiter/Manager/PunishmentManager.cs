using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox;
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
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public enum PunishReason
        {
            SingleRuntimeOverLimit,
            AverageRuntimeOverLimit,
            CombinedRuntimeOverLimit,
            CombinedAverageRuntimeOverLimit,
            ExtremeUsage
        }
        
        public static async Task CheckForPunishment(TrackedPlayer player, long pbEntityId, double lastRunTimeMS)
        {
            if (!Config.Enabled) return;
            
            TrackedPBBlock? trackedPbBlock = player.GetTrackedPB(pbEntityId);
            if (trackedPbBlock is null)
            {
                Log.Error("Null TrackedPBBlock!");
                return;
            }
            
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
            
            // InstaKill
            if (lastRunTimeMS >= Config.InstantKillThreshold)
            {
                await PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, "[" + lastRunTimeMS.ToString(CultureInfo.InvariantCulture) + "ms]");
                Log.Error($"INSTANT KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} with a run time of {lastRunTimeMS}ms");
                return;
            }
            
            if (lastRunTimeMS > allowedRunTime)
            {
                if (trackedPbBlock.Offences.TryPeek(out long lastOffence))
                {
                    if ((Stopwatch.GetTimestamp() - lastOffence) / Stopwatch.Frequency <= Config.GraceAfterOffence)
                        return;
                    
                    trackedPbBlock.Offences.Push(Stopwatch.GetTimestamp());
                    Log.Trace($"PB Block: {trackedPbBlock.ProgrammableBlock?.EntityId} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} has exceeded the run time limit.  Last Run Time: {lastRunTimeMS}ms  Allowed Run Time: {allowedRunTime}ms  Offenses: {trackedPbBlock.Offences.Count}");
                }
                
                // Clean up old offences
                long[] offences = trackedPbBlock.Offences.ToArray();
                
                foreach (long offence in offences)
                {
                    if ((Stopwatch.GetTimestamp() - offence) / Stopwatch.Frequency > Config.OffenseDurationBeforeDeletion)
                        trackedPbBlock.Offences.TryPop(out _);
                    else
                        break;
                }
                    
                // Check if we have enough offences to punish
                if ((Stopwatch.GetTimestamp() - lastOffence) / Stopwatch.Frequency <= 5) return;
                    
                trackedPbBlock.Offences.Push(Stopwatch.GetTimestamp());
                if (offences.Length < Config.MaxOffencesBeforePunishment) return;
                await PunishPB(player, trackedPbBlock, PunishReason.SingleRuntimeOverLimit, null, "[" +trackedPbBlock.LastRunTimeMS.ToString(CultureInfo.InvariantCulture) + "ms]");
            }
            
            if (trackedPbBlock.RunTimeMSAvg > allowedRunTimeAvg)
            {
                if (trackedPbBlock.Offences.TryPeek(out long lastOffence))
                {
                    if ((Stopwatch.GetTimestamp() - lastOffence) / Stopwatch.Frequency <= Config.GraceAfterOffence)
                        return;
                    
                    trackedPbBlock.Offences.Push(Stopwatch.GetTimestamp());
                    Log.Trace($"PB Block: {trackedPbBlock.ProgrammableBlock?.EntityId} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} has exceeded the average run time limit.  Last Run Time: {lastRunTimeMS}ms  Average Run Time: {trackedPbBlock.RunTimeMSAvg}ms");
                }
                
                // Clean up old offences
                long[] offences = trackedPbBlock.Offences.ToArray();
                foreach (long offence in offences)
                {
                    if ((Stopwatch.GetTimestamp() - offence) / Stopwatch.Frequency > Config.OffenseDurationBeforeDeletion)
                        trackedPbBlock.Offences.TryPop(out _);
                    else
                        break;
                }
                    
                // Check if we have enough offences to punish
                if ((Stopwatch.GetTimestamp() - lastOffence) / Stopwatch.Frequency <= 5) return;
                    
                trackedPbBlock.Offences.Push(Stopwatch.GetTimestamp());
                if (offences.Length < Config.MaxOffencesBeforePunishment) return;
                await PunishPB(player, trackedPbBlock, PunishReason.AverageRuntimeOverLimit, null, "[" +trackedPbBlock.RunTimeMSAvg.ToString(CultureInfo.InvariantCulture) + "ms]");
            }
        }
        
        public static Task PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, Punishment? punishment = null, string? notes = null)
        {
            if (trackedPbBlock.ProgrammableBlock is null)
            {
                Log.Warn("Attempted to punish a programmable block that was null!  Grid: " + trackedPbBlock.GridName);
                return Task.CompletedTask;
            }
            
            if ((Stopwatch.GetTimestamp() - trackedPbBlock.LastOffenceTick)/Stopwatch.Frequency <= 1 )
                return Task.CompletedTask;
            
            int gracePeriodSeconds = Config.GracefulShutDownRequestDelay;
            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer? privilegedPlayer))
                gracePeriodSeconds = privilegedPlayer.GracefulShutDownRequestDelay;
            
            if (gracePeriodSeconds > 0)
            {
                trackedPbBlock.ProgrammableBlock.Run($"GracefulShutDown::{gracePeriodSeconds}", UpdateType.Script);
                Task.Delay(Config.GracefulShutDownRequestDelay * 1000).ContinueWith(_ =>
                {
                    PerformPunish(trackedPbBlock, player, reason, null, notes);
                });
                return Task.CompletedTask;
            }
            
            PerformPunish(trackedPbBlock, player, reason, punishment, notes);
            return Task.CompletedTask;
        }

        private static void PerformPunish(TrackedPBBlock trackedPbBlock, TrackedPlayer player, PunishReason reason, Punishment? punishment = null, string? notes = null)
        {
            if (trackedPbBlock.ProgrammableBlock is null)
            {
                Log.Warn("Attempted to punish a programmable block that was null!  Grid: " + trackedPbBlock.GridName);
                return;
            }
            
            Log.Trace($"Punishing PB Block: {trackedPbBlock.ProgrammableBlock.EntityId} on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName} for {reason}. {notes}");
            trackedPbBlock.LastOffenceTick = Stopwatch.GetTimestamp();
            
            switch (punishment)
            {
                case null:
                    switch (Config.Punishment)
                    {
                        case Punishment.TurnOff:
                            string message1 = CreateUserMessage(player, trackedPbBlock, reason, notes);
                            if (message1.Length > 0) Chat.Send(message1, player.SteamId, Color.Red);
                            Log.Info($"Turning off {player.PlayerName} for excessive pb run time on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName}");
                            
                            MySandboxGame.Static.Invoke(() =>
                            {
                                trackedPbBlock.ProgrammableBlock.Enabled = false;
                            }, "Advanced_PB_Limiter");
                            break;
                
                        case Punishment.Destroy:
                            string message2 = CreateUserMessage(player, trackedPbBlock, reason, notes);
                            if (message2.Length > 0) Chat.Send(message2, player.SteamId, Color.Red);
                            Log.Info($"Destroying {player.PlayerName} for excessive pb run time on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName}");
                            float damageAmount = trackedPbBlock.ProgrammableBlock.SlimBlock.Integrity - 1;
                            
                            MySandboxGame.Static.Invoke(() =>
                            {
                                trackedPbBlock.ProgrammableBlock.SlimBlock.DecreaseMountLevel(damageAmount, null);
                            }, "Advanced_PB_Limiter");
                            break;
                
                        case Punishment.Damage:
                            string message3 = CreateUserMessage(player, trackedPbBlock, reason, notes);
                            if (message3.Length > 0) Chat.Send(message3, player.SteamId, Color.Red);
                            Log.Info($"Damaging {player.PlayerName} for excessive pb run time on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName}");
                            
                            MySandboxGame.Static.Invoke(() =>
                            {
                                trackedPbBlock.ProgrammableBlock?.SlimBlock.DoDamage(Config.PunishDamageAmount, MyDamageType.Destruction);
                            }, "Advanced_PB_Limiter");
                            break;
                    }
                    break;
                
                // Punishments that are not the default punishment
                case Punishment.TurnOff:
                    string message4 = CreateUserMessage(player, trackedPbBlock, reason, notes);
                    if (message4.Length > 0) Chat.Send(message4, player.SteamId, Color.Red);
                    MySandboxGame.Static.Invoke(() =>
                    {
                        trackedPbBlock.ProgrammableBlock.Enabled = false;
                    }, "Advanced_PB_Limiter");    
                    break;
                
                case Punishment.Destroy:
                    string message5 = CreateUserMessage(player, trackedPbBlock, reason, notes);
                    if (message5.Length > 0) Chat.Send(message5, player.SteamId, Color.Red);
                    MySandboxGame.Static.Invoke(() =>
                    {
                        trackedPbBlock.ProgrammableBlock.SlimBlock.DecreaseMountLevel(trackedPbBlock.ProgrammableBlock.SlimBlock.Integrity - 1, null);
                    }, "Advanced_PB_Limiter");
                    
                    break;
            }
        }
        
        private static string CreateUserMessage(TrackedPlayer player, TrackedPBBlock pbBlock, PunishReason reason, string? message = null )
        {
            if (player.SteamId == 0 || MySession.Static.Players.IdentityIsNpc(player.PlayerId) || !MySession.Static.Players.IsPlayerOnline(player.PlayerId))
            {
                Log.Warn($"No punishment message was sent: SteamID[{player.SteamId}]  IsNPC[{MySession.Static.Players.IdentityIsNpc(player.PlayerId)}]  IsOnline[{MySession.Static.Players.IsPlayerOnline(player.PlayerId)}]  GridName[{pbBlock.GridName}]");
                return "";
            }
            
            switch (reason)
            {
                case PunishReason.AverageRuntimeOverLimit:
                    return $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to excessive average run time. " + message;

                case PunishReason.CombinedAverageRuntimeOverLimit:
                    return $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to excessive combined average run time. " + message;
                
                case PunishReason.SingleRuntimeOverLimit:
                    return $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to excessive run time. " + message;
                
                case PunishReason.CombinedRuntimeOverLimit:
                    return $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to excessive combined run time. " + message;
                
                case PunishReason.ExtremeUsage:
                    return $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to extreme usage. " + message;
                
                default:
                    return $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished for some reason, please check with server admins. " + message;
            }
        }
    }
}