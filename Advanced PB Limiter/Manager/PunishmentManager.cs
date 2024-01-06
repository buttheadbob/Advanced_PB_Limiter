using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
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
        private static FieldInfo? needsInstansiationField;
        private static MethodInfo? Terminate;
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
            
            // InstaKill
            if (lastRunTimeMS >= Config.InstantKillThreshold)
            {
                await PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, "[" + lastRunTimeMS.ToString(CultureInfo.InvariantCulture) + "ms]");
                Log.Error($"INSTANT KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} with a run-time of {lastRunTimeMS}ms");
                return;
            }

            double allowedRunTime = Config.MaxRunTimeMS;
            double allowedRunTimeAvg = Config.MaxRunTimeMSAvg;
            int allowedOffenses = Config.MaxOffencesBeforePunishment;
            bool combinedLimitRestriction = Config.CheckAllUserBlocksCombined;

            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer privilegedPlayer))
            {
                allowedRunTime = privilegedPlayer.RuntimeAllowance;
                allowedRunTimeAvg = privilegedPlayer.RuntimeAverageAllowance;
                allowedOffenses = privilegedPlayer.OffencesBeforePunishment;
                combinedLimitRestriction = privilegedPlayer.NoCombinedLimits;
            }
            
            // Clean up old offenses
            while (true)
            {
                if (trackedPbBlock.Offences.TryPeek(out long lastOffense_cleanup))
                {                                                                           // Limit is in minutes
                    if (((Stopwatch.GetTimestamp() - lastOffense_cleanup) / Stopwatch.Frequency) / 60 > Config.OffenseDurationBeforeDeletion)
                        if (trackedPbBlock.Offences.TryPop(out _)) continue;
                    
                }

                break;
            }

            // Check if enough time since last offense has passed.
            trackedPbBlock.Offences.TryPeek(out long lastOffense);
            if ((Stopwatch.GetTimestamp() - lastOffense) / Stopwatch.Frequency <= Config.GraceAfterOffence)
                return;
            
            if (allowedRunTime != 0 && lastRunTimeMS > allowedRunTime)
            {
                trackedPbBlock.Offences.Push(Stopwatch.GetTimestamp());
                Log.Trace($"PB Block: {trackedPbBlock.ProgrammableBlock?.EntityId} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} has exceeded the run-time limit.  Last Run Time: {lastRunTimeMS}ms  Allowed Run Time: {allowedRunTime}ms  Offenses: {trackedPbBlock.Offences.Count}");
                
                if (trackedPbBlock.Offences.Count <= allowedOffenses)
                {
                    Chat.Send($"You have received an offense [{trackedPbBlock.Offences.Count} of {allowedOffenses}] for exceeding your run-time limit [{allowedRunTime}] on grid {trackedPbBlock.GridName}.  Please reduce the run time of your Programmable Block or it will be punished.", player.SteamId, Color.Red);
                    return;
                }
                
                await PunishPB(player, trackedPbBlock, PunishReason.SingleRuntimeOverLimit, null, "[" +trackedPbBlock.LastRunTimeMS.ToString(CultureInfo.InvariantCulture) + "ms]");
                return; // Block is punished, no need to punish again with average run-time violations.
            }
            
            if (combinedLimitRestriction && trackedPbBlock.RunTimeMSAvg > allowedRunTimeAvg)
            {
                trackedPbBlock.Offences.Push(Stopwatch.GetTimestamp());
                Log.Trace($"PB Block: {trackedPbBlock.ProgrammableBlock?.EntityId} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} has exceeded the average run-time limit.  Last Run-Time: {lastRunTimeMS}ms  Average Run Time: {trackedPbBlock.RunTimeMSAvg}ms");
                
                if (trackedPbBlock.Offences.Count < Config.MaxOffencesBeforePunishment) return;
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
            
            int gracePeriodSeconds = Config.GracefulShutDownRequestDelay;
            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer privilegedPlayer))
                gracePeriodSeconds = privilegedPlayer.GracefulShutDownRequestDelay;
            MySandboxGame.Static.Invoke(() => // Not sure if this actually needs to be on the game thread, but it's better to be safe than sorry.
            {
                trackedPbBlock.ProgrammableBlock.Run($"GracefulShutDown::{gracePeriodSeconds}", UpdateType.Script);
            }, "Advanced_PB_Limiter");
            
            if (reason == PunishReason.ExtremeUsage)
            {
                PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                return Task.CompletedTask;
            }
            
            Task.Delay(gracePeriodSeconds * 1000).ContinueWith(_ =>
            {
                PerformPunish(trackedPbBlock, player, reason, null, notes);
            });
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

            punishment ??= Config.Punishment;
            
            switch (punishment)
            {
                case Punishment.TurnOff:
                    PerformPunishment_TurnOff(player, trackedPbBlock, reason, notes);
                    return;
                
                case Punishment.Destroy:
                    PerformPunishment_Destroy(player, trackedPbBlock, reason, notes);
                    return;
                
                case Punishment.Damage:
                    PerformPunishment_Damage(player, trackedPbBlock, reason, notes);
                    return;
            }
        }

        private static void PerformPunishment_TurnOff(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, string? notes = null)
        {
            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, notes), player.SteamId, Color.Red);
            // Reflect only when needed.
            needsInstansiationField = typeof(MyProgrammableBlock).GetField("m_needsInstantiation", BindingFlags.NonPublic | BindingFlags.Instance);
            Terminate = typeof(MyProgrammableBlock).GetMethod("OnProgramTermination", BindingFlags.NonPublic | BindingFlags.Instance);
            
            // Turn off the block
            if (trackedPbBlock.ProgrammableBlock is not null)
                MySandboxGame.Static.Invoke(() =>
                {
                    needsInstansiationField?.SetValue(trackedPbBlock.ProgrammableBlock, false);
                    Terminate?.Invoke(trackedPbBlock.ProgrammableBlock, new object[] {  MyProgrammableBlock.ScriptTerminationReason.InstructionOverflow});
                    trackedPbBlock.ProgrammableBlock.Enabled = false;
                }, "Advanced_PB_Limiter");    
        }
        
        private static void PerformPunishment_Destroy(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, string? notes = null)
        {
            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, notes), player.SteamId, Color.Red);
            // Reflect only when needed.
            needsInstansiationField = typeof(MyProgrammableBlock).GetField("m_needsInstantiation", BindingFlags.NonPublic | BindingFlags.Instance);
            Terminate = typeof(MyProgrammableBlock).GetMethod("OnProgramTermination", BindingFlags.NonPublic | BindingFlags.Instance);
            
            // Destroy the block
            if (trackedPbBlock.ProgrammableBlock is not null)
                MySandboxGame.Static.Invoke(() =>
                {
                    try
                    { 
                        needsInstansiationField?.SetValue(trackedPbBlock.ProgrammableBlock, false);
                        trackedPbBlock.ProgrammableBlock.SlimBlock.DoDamage(trackedPbBlock.ProgrammableBlock.SlimBlock.BuildIntegrity - 1, MyDamageType.Unknown, true, null, 0);
                    }
                    catch (Exception e)
                    {
                        Log.Error("Error while destroying a programmable block: " + e);
                    }
                    
                }, "Advanced_PB_Limiter");
        }
        
        private static void PerformPunishment_Damage(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, string? notes = null)
        {
            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, notes), player.SteamId, Color.Red);
            // Reflect only when needed.
            needsInstansiationField = typeof(MyProgrammableBlock).GetField("m_needsInstantiation", BindingFlags.NonPublic | BindingFlags.Instance);
            Terminate = typeof(MyProgrammableBlock).GetMethod("OnProgramTermination", BindingFlags.NonPublic | BindingFlags.Instance);
            
            // Damage the block
            if (trackedPbBlock.ProgrammableBlock is not null)
                MySandboxGame.Static.Invoke(() =>
                {
                    needsInstansiationField?.SetValue(trackedPbBlock.ProgrammableBlock, false);
                    Terminate?.Invoke(trackedPbBlock.ProgrammableBlock, new object[] {  MyProgrammableBlock.ScriptTerminationReason.InstructionOverflow});
                    trackedPbBlock.ProgrammableBlock.SlimBlock.DoDamage(Config.PunishDamageAmount, MyDamageType.Radioactivity, true, null, 0);
                }, "Advanced_PB_Limiter");
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