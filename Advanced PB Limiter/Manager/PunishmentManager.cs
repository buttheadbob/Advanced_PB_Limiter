using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
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
        private static ConcurrentDictionary<long, ulong> GracefulShutdownsInProgress { get; } = new(); // Key: EntityId, Value: SteamId
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
            if (GracefulShutdownsInProgress.TryGetValue(pbEntityId, out _)) return;
            
            TrackedPBBlock? trackedPbBlock = player.GetTrackedPB(pbEntityId);
            if (trackedPbBlock is null)
            {
                Log.Error("Null TrackedPBBlock!");
                return;
            }
            
            // InstaKill by run-time
            if (Config.InstantKillThreshold > 0 && lastRunTimeMS > Config.InstantKillThreshold)
            {
                await PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, $"[{lastRunTimeMS:0.0000}ms]");
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} with a run-time of {lastRunTimeMS:0.0000}ms");
                return;
            }
            
            // InstaKill by memory
            if (Config.PBMemoryThreshold < trackedPbBlock.memoryUsage)
            {
                string usage = HelperUtils.FormatBytesToKB(trackedPbBlock.memoryUsage);
                await PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, $"[{usage}]");
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} with a memory usage of {usage}");
                return;
            }

            if (trackedPbBlock.IsUnderGracePeriod(player.SteamId)) return;
            
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
                        if (trackedPbBlock.Offences.TryDequeue(out _)) continue;
                }

                break;
            }

            // Check if enough time since last offense has passed.
            if ((Stopwatch.GetTimestamp() - trackedPbBlock.LastOffenceTick) / Stopwatch.Frequency <= Config.GraceAfterOffence)
                return;
            
            if (allowedRunTime != 0 && lastRunTimeMS > allowedRunTime)
            {
                trackedPbBlock.Offences.Enqueue(Stopwatch.GetTimestamp());
                trackedPbBlock.LastOffenceTick = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref player.Offences);
                Log.Info($"PB Block: {trackedPbBlock.ProgrammableBlock?.EntityId} for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock?.CustomName}] has exceeded the run-time limit.  Last Run-Time: {lastRunTimeMS:0.0000}ms  Allowed-Run-Time: {allowedRunTime:0.0000}ms  Offenses: {trackedPbBlock.Offences.Count}");
                
                if (trackedPbBlock.Offences.Count <= allowedOffenses)
                {
                    if (Config.WarnUserOnOffense)
                        Chat.Send($"You have received an offense [{trackedPbBlock.Offences.Count} of {allowedOffenses}] for exceeding your run-time limit [{allowedRunTime:0.0000}ms] for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock?.CustomName}].  Please reduce the run time of your Programmable Block or it will be punished.", player.SteamId, Color.Red);
                    return;
                }
                StringBuilder sb = new();
                sb.AppendLine();
                sb.AppendLine($"Your Limit: [{allowedRunTime:0.0000}ms]");
                sb.AppendLine($"Your Usage: [{lastRunTimeMS:0.0000}ms]");
                await PunishPB(player, trackedPbBlock, PunishReason.SingleRuntimeOverLimit, null, sb.ToString());
                return; // Block is punished, no need to punish again with average run-time violations.
            }
            
            if (combinedLimitRestriction && trackedPbBlock.RunTimeMSAvg > allowedRunTimeAvg)
            {
                trackedPbBlock.Offences.Enqueue(Stopwatch.GetTimestamp());
                trackedPbBlock.LastOffenceTick = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref player.Offences);
                Log.Info($"PB Block: {trackedPbBlock.ProgrammableBlock?.EntityId} for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock?.CustomName}] has exceeded the average-run-time limit.  Allowed Average-Run-Time: {allowedRunTimeAvg:0.0000}ms   Last Run-Time: {lastRunTimeMS:0.0000}ms   Average-Run-Time: {trackedPbBlock.RunTimeMSAvg:0.0000}ms  Offenses: {trackedPbBlock.Offences.Count}");
                
                if (trackedPbBlock.Offences.Count <= allowedOffenses)
                {
                    if (Config.WarnUserOnOffense)
                        Chat.Send($"You have received an offense [{trackedPbBlock.Offences.Count} of {allowedOffenses}] for exceeding your average-run-time limit [{allowedRunTimeAvg:0.0000}ms] for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock?.CustomName}].  Please reduce the run time of your Programmable Block or it will be punished.", player.SteamId, Color.Red);
                    return;
                }
                
                if (trackedPbBlock.Offences.Count <= Config.MaxOffencesBeforePunishment) return;
                StringBuilder sb = new();
                sb.AppendLine();
                sb.AppendLine($"Your Limit: [{allowedRunTimeAvg:0.0000}ms]");
                sb.AppendLine($"Your Usage: [{trackedPbBlock.RunTimeMSAvg:0.0000}ms]");
                await PunishPB(player, trackedPbBlock, PunishReason.AverageRuntimeOverLimit, null, sb.ToString());
            }
        }
        
        public static Task PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, Punishment? punishment = null, string? notes = null)
        {
            if (trackedPbBlock.ProgrammableBlock is null)
            {
                Log.Warn($"Attempted to punish a programmable block that was null! for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock?.CustomName}]");
                return Task.CompletedTask;
            }
            
            int gracePeriodSeconds = Config.GracefulShutDownRequestDelay;
            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer privilegedPlayer))
                gracePeriodSeconds = privilegedPlayer.GracefulShutDownRequestDelay;
            
            if (reason == PunishReason.ExtremeUsage)
            {
                PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                return Task.CompletedTask;
            }
            
            if (gracePeriodSeconds == 0)
            {
                PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                return Task.CompletedTask;
            }

            GracefulShutdownsInProgress.TryAdd(trackedPbBlock.ProgrammableBlock.EntityId, player.SteamId);
            
            MySandboxGame.Static.Invoke(() => // Not sure if this actually needs to be on the game thread.
            {
                trackedPbBlock.ProgrammableBlock.Run($"GracefulShutDown::{gracePeriodSeconds}", UpdateType.Script);
            }, "Advanced_PB_Limiter");
            
            Task.Delay(gracePeriodSeconds * 1000).ContinueWith(_ =>
            {
                PerformPunish(trackedPbBlock, player, reason, null, notes);
            });
            GracefulShutdownsInProgress.TryRemove(trackedPbBlock.ProgrammableBlock.EntityId, out _);
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

            return reason switch
            {
                PunishReason.AverageRuntimeOverLimit => $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to excessive average run time. " + message,
                PunishReason.CombinedAverageRuntimeOverLimit => $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to excessive combined average run time. " + message,
                PunishReason.SingleRuntimeOverLimit => $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to excessive run time. " + message,
                PunishReason.CombinedRuntimeOverLimit => $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to excessive combined run time. " + message,
                PunishReason.ExtremeUsage => $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished due to extreme usage. " + message,
                _ => $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock?.CubeGrid.DisplayName}> was punished for some reason, please check with server admins. " + message
            };
        }
    }
}