using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using Torch.Utils;
using VRage.Collections;
using VRage.Game;
using VRage.Game.ObjectBuilders.Components;
using VRage.Network;
using VRage.Replication;
using VRageMath;
using static Advanced_PB_Limiter.Utils.Enums;
using Sandbox.Game.Replication;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.Manager
{
    public static class PunishmentManager
    {
        [ReflectedGetter(Name = "m_clientStates")]
        private static Func<MyReplicationServer, IDictionary> _clientStates;
        private const string CLIENT_DATA_TYPE_NAME = "VRage.Network.MyClient, VRage";
        [ReflectedGetter(TypeName = CLIENT_DATA_TYPE_NAME, Name = "Replicables")]
        private static Func<object, MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>> _replicables;
        [ReflectedMethod(Name = "RemoveForClient", OverrideTypeNames = new[] { null, CLIENT_DATA_TYPE_NAME, null })]
        private static Action<MyReplicationServer, IMyReplicable, object, bool> _removeForClient;
        [ReflectedMethod(Name = "ForceReplicable")]
        private static Action<MyReplicationServer, IMyReplicable, Endpoint> _forceReplicable;
        
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
        
        public static void CheckForPunishment(TrackedPlayer player, long pbEntityId, double lastRunTimeMS)
        {
            if (!Config.Enabled) return;
            if (GracefulShutdownsInProgress.TryGetValue(pbEntityId, out _)) return;
            
            TrackedPBBlock? trackedPbBlock = player.GetTrackedPB(pbEntityId);
            if (trackedPbBlock is null)
            {
                Log.Error("Null TrackedPBBlock!");
                return;
            }

            if (trackedPbBlock.IsUnderGracePeriod(player.SteamId)) return;
            
            // InstaKill by run-time
            if (Config.InstantKillThreshold > 0 && lastRunTimeMS > Config.InstantKillThreshold)
            {
                PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, $"[{lastRunTimeMS:0.0000}ms]");
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} with a run-time of {lastRunTimeMS:0.0000}ms");
                return;
            }
            
            // InstaKill by memory
            if (Config.PBMemoryThreshold != 0 && Config.PBMemoryThreshold < trackedPbBlock.memoryUsage)
            {
                string usage = HelperUtils.FormatBytesToKB(trackedPbBlock.memoryUsage);
                PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, $"[{usage}]");
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} with a memory usage of {usage}");
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
                {                           // Limit is in minutes
                    if (lastOffense_cleanup.TicksTillNow_TimeSpan().TotalMinutes > Config.OffenseDurationBeforeDeletion)
                        if (trackedPbBlock.Offences.TryDequeue(out _)) continue;
                }

                break;
            }

            // Check if enough time since last offense has passed.
            if (trackedPbBlock.LastOffenceTick.TicksTillNow_TimeSpan().TotalSeconds <= Config.GraceAfterOffence)
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

                    if (trackedPbBlock.Offences.Count == allowedOffenses)
                    {
                        MySandboxGame.Static.Invoke(() => // Not sure if this actually needs to be forced on the game thread.
                        {
                            trackedPbBlock.ProgrammableBlock?.Run($"GracefulShutDown::-1", UpdateType.Script);
                        }, "Advanced_PB_Limiter");
                    }
                    return;
                }
                
                StringBuilder sb = new();
                sb.AppendLine();
                sb.AppendLine($"Your Limit: [{allowedRunTime:0.0000}ms]");
                sb.AppendLine($"Your Usage: [{lastRunTimeMS:0.0000}ms]");
                PunishPB(player, trackedPbBlock, PunishReason.SingleRuntimeOverLimit, null, sb.ToString());
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
                    
                    if (trackedPbBlock.Offences.Count == allowedOffenses)
                    {
                        MySandboxGame.Static.Invoke(() => // Not sure if this actually needs to be forced on the game thread.
                        {
                            trackedPbBlock.ProgrammableBlock?.Run($"GracefulShutDown::LastWarning", UpdateType.Script);
                        }, "Advanced_PB_Limiter");
                    }
                    return;
                }
                
                if (trackedPbBlock.Offences.Count <= Config.MaxOffencesBeforePunishment) return;
                StringBuilder sb = new();
                sb.AppendLine();
                sb.AppendLine($"Your Limit: [{allowedRunTimeAvg:0.0000}ms]");
                sb.AppendLine($"Your Usage: [{trackedPbBlock.RunTimeMSAvg:0.0000}ms]");
                PunishPB(player, trackedPbBlock, PunishReason.AverageRuntimeOverLimit, null, sb.ToString());
            }
        }
        
        public static void PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, Punishment? punishment = null, string? notes = null)
        {
            if (trackedPbBlock.ProgrammableBlock is null)
            {
                Log.Warn($"Attempted to punish a programmable block that was null! for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock?.CustomName}]");
                return;
            }
            
            int gracePeriodSeconds = Config.GracefulShutDownRequestDelay;
            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer privilegedPlayer))
                gracePeriodSeconds = privilegedPlayer.GracefulShutDownRequestDelay;
            
            if (reason == PunishReason.ExtremeUsage)
            {
                PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                return;
            }
            
            if (gracePeriodSeconds == 0)
            {
                PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                return;
            }

            GracefulShutdownsInProgress.TryAdd(trackedPbBlock.ProgrammableBlock.EntityId, player.SteamId);
            
            MySandboxGame.Static.Invoke(() => // Not sure if this actually needs to be forced on the game thread.
            {
                trackedPbBlock.ProgrammableBlock.Run($"GracefulShutDown::{gracePeriodSeconds}", UpdateType.Script);
            }, "Advanced_PB_Limiter");
            
            Task.Delay(gracePeriodSeconds * 1000).ContinueWith(_ =>
            {
                PerformPunish(trackedPbBlock, player, reason, null, notes);
            });
            GracefulShutdownsInProgress.TryRemove(trackedPbBlock.ProgrammableBlock.EntityId, out _);
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
                    trackedPbBlock.ProgrammableBlock.Enabled = false;
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
            {
                MySandboxGame.Static.Invoke(() =>
                {
                    try
                    {
                        trackedPbBlock.ProgrammableBlock.Enabled = false;
                        needsInstansiationField?.SetValue(trackedPbBlock.ProgrammableBlock, false);
                        Terminate?.Invoke(trackedPbBlock.ProgrammableBlock, new object[] {  MyProgrammableBlock.ScriptTerminationReason.InstructionOverflow});
                        
                        // Check for SafeZone
                        // Yes, I tried the other method, MySessionComponentSafeZones... but it doesn't always return correctly. 

                        BoundingBoxD blockBoundingBoxD = trackedPbBlock.ProgrammableBlock.SlimBlock.WorldAABB;
                        List<MyEntity>? foundEntities = MyEntities.GetEntitiesInAABB(ref blockBoundingBoxD);
                        MySafeZone? safeZone = null;
                        foreach (MyEntity foundEntity in foundEntities)
                        {
                            if (foundEntity is not MySafeZone foundZone) continue;
                            safeZone = foundZone;
                            break;
                        }

                        if (safeZone is not null && (safeZone.AllowedActions & MySafeZoneAction.Damage) != MySafeZoneAction.Damage)
                        {
                            string blockDisplayName = trackedPbBlock.ProgrammableBlock.CustomName.ToString(); // need this for resync!
                            trackedPbBlock.ProgrammableBlock.Enabled = false; // Turn off because it won't need a recompile and restarts when rebuilt by player.
                            trackedPbBlock.ProgrammableBlock.SlimBlock.FullyDismount(new MyInventory());
                            PunishReSync(blockDisplayName);
                        }
                        else
                            trackedPbBlock.ProgrammableBlock.SlimBlock.DoDamage(trackedPbBlock.ProgrammableBlock.SlimBlock.BuildIntegrity - 100, MyDamageType.Unknown, true, null, 0);
                    }
                    catch (Exception e)
                    {
                        Log.Error("Error while destroying a programmable block: " + e);
                    }

                }, "Advanced_PB_Limiter");
            }
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

                    BoundingBoxD blockBoundingBoxD = trackedPbBlock.ProgrammableBlock.SlimBlock.WorldAABB;
                    List<MyEntity>? foundEntities = MyEntities.GetEntitiesInAABB(ref blockBoundingBoxD);
                    MySafeZone? safeZone = null;
                    foreach (MyEntity foundEntity in foundEntities)
                    {
                        if (foundEntity is not MySafeZone foundZone) continue;
                        safeZone = foundZone;
                        break;
                    }
                    
                    if (safeZone is not null && (safeZone.AllowedActions & MySafeZoneAction.Damage) != MySafeZoneAction.Damage)
                    {
                        if (Config.KillBlockProtectedInSafeZone == false) return;
                        
                        string blockDisplayName = trackedPbBlock.ProgrammableBlock.CustomName.ToString(); // need this for resync!
                        trackedPbBlock.ProgrammableBlock.Enabled = false; // Turn off because it won't need a recompile and restarts when rebuilt by player.
                        trackedPbBlock.ProgrammableBlock.SlimBlock.FullyDismount(new MyInventory());
                        PunishReSync(blockDisplayName);
                        return;
                    }
                    
                    trackedPbBlock.ProgrammableBlock.SlimBlock.DoDamage(Config.PunishDamageAmount, MyDamageType.Unknown, true, null, 0);
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

        private static void PunishReSync(string blockDisplayName)
        {
            // Update state to all players, by Jim and slight modification by Senx for better performance.
            MyReplicationServer? replicationServer = (MyReplicationServer)MyMultiplayer.ReplicationLayer;
            foreach (MyPlayer onlinePlayer in MySession.Static.Players.GetOnlinePlayers())
            {
                Endpoint playerEndpoint = new (onlinePlayer.Client.SteamUserId, 0);
                IDictionary? clientDataDict = _clientStates.Invoke(replicationServer);
                object clientData;
                try
                {
                    clientData = clientDataDict[playerEndpoint];
                }
                catch
                {
                    return;
                }
                    
                MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>? clientReplicables = _replicables.Invoke(clientData);

                List<IMyReplicable> replicableList = new (clientReplicables.Count);
                foreach (KeyValuePair<IMyReplicable, MyReplicableClientData> pair in clientReplicables)
                {
                    ReadOnlySpan<char> instanceName = pair.Key.InstanceName.AsSpan();
                    if (instanceName.IsEmpty) continue;
                    if (!instanceName.StartsWith("MyP".AsSpan())) continue; // Quick and Dirty
                    if (!instanceName.StartsWith("MyProgrammableBlock".AsSpan())) continue;
                    if (!instanceName.EndsWith(blockDisplayName.AsSpan())) continue;
                    replicableList.Add(pair.Key);
                }

                foreach (IMyReplicable? replicable in replicableList)
                {
                    _removeForClient.Invoke(replicationServer, replicable, clientData, true);
                    _forceReplicable.Invoke(replicationServer, replicable, playerEndpoint);
                }
            }
        }
    }
}