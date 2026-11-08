using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Patches;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage.Collections;
using VRage.Game;
using VRage.Game.ObjectBuilders.Components;
using VRage.Network;
using VRage.Replication;
using VRageMath;
using static Advanced_PB_Limiter.Utils.Enums;
using VRage.Game.Entity;
// ReSharper disable InconsistentNaming

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>Decides on and hands out punishments. Everything here runs on the game thread.</summary>
    public static class PunishmentManager
    {
        private static readonly LimiterLog Log = new("Punishment Manager");
        private static IPbLimiterConfig Config => LimiterCore.Config;

        /// <summary>At least one minute: with 0 every offence is forgotten as it happens, which turns off offence punishment altogether.</summary>
        internal static int OffenceLifetimeMinutes => Math.Max(1, Config.OffenseDurationBeforeDeletion);

        public enum PunishReason
        {
            SingleRuntimeOverLimit,
            AverageRuntimeOverLimit,
            CombinedRuntimeOverLimit,
            CombinedAverageRuntimeOverLimit,
            ExtremeUsage
        }

        /// <summary>Called after every run of every tracked script. Nothing here may allocate until a limit is broken.</summary>
        public static void CheckForPunishment(TrackedPlayer player, TrackedPBBlock trackedPbBlock, double lastRunTimeMS)
        {
            if (!Config.Enabled) return;
            if (Config.EnforceOnlyBelowSimThreshold && Sync.ServerSimulationRatio >= Config.EnforceOnlyBelowSimRate) return;
            if (trackedPbBlock.ShutdownRequested) return;

            // InstaKill by run-time
            if (Config.InstantKillThreshold > 0 && lastRunTimeMS > Config.InstantKillThreshold)
            {
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName} with a run-time of {lastRunTimeMS:0.0000}ms");
                PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, $"[{lastRunTimeMS:0.0000}ms]");
                return;
            }

            // InstaKill by memory
            if (Config.EnableMemoryMonitoring && Config.PBMemoryThreshold != 0 && Config.PBMemoryThreshold < trackedPbBlock.memoryUsage)
            {
                string usage = HelperUtils.FormatBytesToKB(trackedPbBlock.memoryUsage);
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName} with a memory usage of {usage}");
                PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, $"[{usage}]");
                return;
            }

            PrivilegedPlayer? privilegedPlayer = player.Privileged;
            double allowedRunTime = privilegedPlayer?.RuntimeAllowance ?? Config.MaxRunTimeMS;
            double allowedRunTimeAvg = privilegedPlayer?.RuntimeAverageAllowance ?? Config.MaxRunTimeMSAvg;
            int allowedOffenses = privilegedPlayer?.OffencesBeforePunishment ?? Config.MaxOffencesBeforePunishment;

            trackedPbBlock.ForgetOffencesOlderThan(OffenceLifetimeMinutes);

            // Check if enough time since last offense has passed.
            if (trackedPbBlock.IsInGraceAfterOffence)
                return;

            // Check for runtime offense
            if (allowedRunTime != 0 && lastRunTimeMS > allowedRunTime && !trackedPbBlock.IsOutlier(lastRunTimeMS))
            {
                RecordOffence(trackedPbBlock);
                Log.Info($"PB Block: {trackedPbBlock.ProgrammableBlock.EntityId} for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock.CustomName}] has exceeded the run-time limit.  Last Run-Time: {lastRunTimeMS:0.0000}ms  Allowed-Run-Time: {allowedRunTime:0.0000}ms  Offenses: {trackedPbBlock.Offences.Count}");

                if (trackedPbBlock.Offences.Count <= allowedOffenses)
                {
                    if (Config.WarnUserOnOffense)
                        Chat.Send($"You have received offense [{trackedPbBlock.Offences.Count} of {allowedOffenses}] for exceeding your run-time limit [{allowedRunTime:0.0000}ms] for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock.CustomName}].  Please reduce the run time of your Programmable Block or it will be punished.", player.PlayerId, Color.Red);

                    if (trackedPbBlock.Offences.Count == allowedOffenses)
                        trackedPbBlock.ProgrammableBlock.Run("GracefulShutDown::-1", UpdateType.Script);
                    return;
                }

                PunishPB(player, trackedPbBlock, PunishReason.SingleRuntimeOverLimit, null, UsageNotes(allowedRunTime, lastRunTimeMS));
                return; // Block is punished, no need to punish again with average run-time violations.
            }

            // check for block average runtime offense
            double runTimeMSAvg = trackedPbBlock.RunTimeMSAvg;
            if (allowedRunTimeAvg != 0 && runTimeMSAvg > allowedRunTimeAvg)
            {
                RecordOffence(trackedPbBlock);
                Log.Info($"PB Block: {trackedPbBlock.ProgrammableBlock.EntityId} for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock.CustomName}] has exceeded the average-run-time limit.  Allowed Average-Run-Time: {allowedRunTimeAvg:0.0000}ms   Last Run-Time: {lastRunTimeMS:0.0000}ms   Average-Run-Time: {runTimeMSAvg:0.0000}ms  Offenses: {trackedPbBlock.Offences.Count}");

                if (trackedPbBlock.Offences.Count <= allowedOffenses)
                {
                    if (Config.WarnUserOnOffense)
                        Chat.Send($"You have received offense [{trackedPbBlock.Offences.Count} of {allowedOffenses}] for exceeding your average-run-time limit [{allowedRunTimeAvg:0.0000}ms] for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock.CustomName}].  Please reduce the run time of your Programmable Block or it will be punished.", player.PlayerId, Color.Red);

                    if (trackedPbBlock.Offences.Count == allowedOffenses)
                        trackedPbBlock.ProgrammableBlock.Run("GracefulShutDown::-2", UpdateType.Script);
                    return;
                }

                PunishPB(player, trackedPbBlock, PunishReason.AverageRuntimeOverLimit, null, UsageNotes(allowedRunTimeAvg, runTimeMSAvg));
            }
        }

        private static void RecordOffence(TrackedPBBlock trackedPbBlock)
        {
            long now = Stopwatch.GetTimestamp();
            trackedPbBlock.Offences.Enqueue(now);
            trackedPbBlock.LastOffenceTick = now;
        }

        private static string UsageNotes(double limit, double usage)
        {
            return $"{Environment.NewLine}Your Limit: [{limit:0.0000}ms]{Environment.NewLine}Your Usage: [{usage:0.0000}ms]{Environment.NewLine}";
        }

        public static void PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, Punishment? punishment = null, string? notes = null)
        {
            if (trackedPbBlock.ShutdownRequested) return; // Already on its way.

            int gracePeriodSeconds = player.Privileged?.GracefulShutDownRequestDelay ?? Config.GracefulShutDownRequestDelay;
            if (reason == PunishReason.ExtremeUsage || gracePeriodSeconds <= 0)
            {
                PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                return;
            }

            trackedPbBlock.ShutdownRequested = true;
            trackedPbBlock.ProgrammableBlock.Run($"GracefulShutDown::{gracePeriodSeconds}", UpdateType.Script);

            // Punishments are always applied after the delay, whatever the script did with it.
            Task.Delay(gracePeriodSeconds * 1000).ContinueWith(_ =>
            {
                MySandboxGame.Static.Invoke(() =>
                {
                    trackedPbBlock.ShutdownRequested = false;
                    if (LimiterCore.GameOnline)
                        PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                }, "Advanced_PB_Limiter");
            });
        }

        private static void PerformPunish(TrackedPBBlock trackedPbBlock, TrackedPlayer player, PunishReason reason, Punishment? punishment, string? notes)
        {
            MyProgrammableBlock block = trackedPbBlock.ProgrammableBlock;
            if (block.Closed || block.MarkedForClose) return; // Removed while the script had its grace period.

            Log.Trace($"Punishing PB Block: {block.EntityId} on grid {block.CubeGrid.DisplayName} for {reason}. {notes}");
            trackedPbBlock.LastOffenceTick = Stopwatch.GetTimestamp();

            try
            {
                Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, notes), player.PlayerId, Color.Red);

                switch (punishment ?? player.Privileged?.Punishment ?? Config.Punishment)
                {
                    case Punishment.TurnOff:
                        PerformPunishment_TurnOff(block);
                        return;

                    case Punishment.Destroy:
                        PerformPunishment_Destroy(block);
                        return;

                    case Punishment.Damage:
                        PerformPunishment_Damage(block, player.Privileged?.DamageAmount ?? Config.PunishDamageAmount);
                        return;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Error while punishing a programmable block");
            }
        }

        private static void PerformPunishment_TurnOff(MyProgrammableBlock block)
        {
            block.Enabled = false;
            PbAccess.ClearNeedsInstantiation(block);
            PbAccess.TerminateProgram(block);
            block.Enabled = false;
        }

        private static void PerformPunishment_Destroy(MyProgrammableBlock block)
        {
            block.Enabled = false;
            PbAccess.ClearNeedsInstantiation(block);
            PbAccess.TerminateProgram(block);

            if (IsProtectedFromDamage(block))
                Dismount(block);
            else
                block.SlimBlock.DoDamage(Math.Max(0, block.SlimBlock.BuildIntegrity - 100), MyDamageType.Unknown, true, null, 0);
        }

        private static void PerformPunishment_Damage(MyProgrammableBlock block, int damageAmount)
        {
            PbAccess.ClearNeedsInstantiation(block);

            if (IsProtectedFromDamage(block))
            {
                if (Config.KillProtectedBlock)
                    Dismount(block);
                return;
            }

            block.SlimBlock.DoDamage(damageAmount, MyDamageType.Unknown, true, null, 0);
        }

        private static bool IsProtectedFromDamage(MyProgrammableBlock block)
        {
            // Yes, I tried the other method, MySessionComponentSafeZones... but it doesn't always return correctly.
            BoundingBoxD blockBoundingBoxD = block.SlimBlock.WorldAABB;

            // The game's own shared list, it must be left empty or the next caller gets these entities as well.
            List<MyEntity> foundEntities = MyEntities.GetEntitiesInAABB(ref blockBoundingBoxD);
            try
            {
                foreach (MyEntity foundEntity in foundEntities)
                {
                    if (foundEntity is MySafeZone safeZone)
                        return (safeZone.AllowedActions & MySafeZoneAction.Damage) != MySafeZoneAction.Damage;
                }
                return false;
            }
            finally
            {
                foundEntities.Clear();
            }
        }

        private static void Dismount(MyProgrammableBlock block)
        {
            string blockDisplayName = block.CustomName.ToString(); // need this for resync!
            block.Enabled = false; // Turn off because it won't need a recompile and restarts when rebuilt by player.
            block.SlimBlock.FullyDismount(new MyInventory());
            PunishReSync(blockDisplayName);
        }

        private static string CreateUserMessage(TrackedPlayer player, TrackedPBBlock pbBlock, PunishReason reason, string? message = null )
        {
            bool isNpc = MySession.Static.Players.IdentityIsNpc(player.PlayerId);
            bool isOnline = MySession.Static.Players.IsPlayerOnline(player.PlayerId);
            if (player.SteamId == 0 || isNpc || !isOnline)
            {
                Log.Warn($"No punishment message was sent: SteamID[{player.SteamId}]  IsNPC[{isNpc}]  IsOnline[{isOnline}]  GridName[{pbBlock.GridName}]");
                return "";
            }

            string why = reason switch
            {
                PunishReason.AverageRuntimeOverLimit => "was punished due to excessive average run time. ",
                PunishReason.CombinedAverageRuntimeOverLimit => "was punished due to excessive combined average run time. ",
                PunishReason.SingleRuntimeOverLimit => "was punished due to excessive run time. ",
                PunishReason.CombinedRuntimeOverLimit => "was punished due to excessive combined run time. ",
                PunishReason.ExtremeUsage => "was punished due to extreme usage. ",
                _ => "was punished for some reason, please check with server admins. "
            };

            return $"Your Programmable Block on grid <{pbBlock.ProgrammableBlock.CubeGrid.DisplayName}> {why}{message}";
        }

        private static void PunishReSync(string blockDisplayName)
        {
            // Update state to all players, by Jim and slight modification by Senx.
            MyReplicationServer replicationServer = (MyReplicationServer)MyMultiplayer.ReplicationLayer;
            IDictionary clientDataDict = ReplicationAccess.GetClientStates(replicationServer);

            foreach (MyPlayer onlinePlayer in MySession.Static.Players.GetOnlinePlayers())
            {
                if (onlinePlayer.Client is null) continue; // Not connected, so there is nothing to re-send to.

                Endpoint playerEndpoint = new (onlinePlayer.Client.SteamUserId, 0);
                object? clientData = clientDataDict[playerEndpoint];
                if (clientData is null) continue;

                MyConcurrentDictionary<IMyReplicable, MyReplicableClientData> clientReplicables = ReplicationAccess.GetReplicables(clientData);

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

                foreach (IMyReplicable replicable in replicableList)
                {
                    ReplicationAccess.ReSend(replicationServer, replicable, clientData, playerEndpoint);
                }
            }
        }
    }
}
