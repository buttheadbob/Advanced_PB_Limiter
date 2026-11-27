#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Patches;
using Advanced_PB_Limiter.QuasarContracts;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
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

        // The blocks waiting out a grace period, by entity id, each with its grace period. Kept by block rather than on its
        // tracking entry: a block handed to someone else keeps its entry, but one whose entry was dropped meanwhile, resting
        // under its new owner past the player keep time say, is tracked again with a fresh entry, and must not be punished
        // again on top. Cleared when the world unloads, as its grace periods are dropped then and an entity id comes back
        // with the world; a grace period removes only itself, so one from before a reload never removes a newer one. Added
        // and taken by an instant kill on the game thread; removed there or, on failure, from any thread.
        private static readonly ConcurrentDictionary<long, Grace> AwaitingGrace = new();

        /// <summary>A punishment waiting out its grace period: who it is for and with what, as PerformPunish resolves it.</summary>
        private sealed class Grace
        {
            public Grace(TrackedPlayer player, Punishment? punishment)
            {
                Player = player;
                Punishment = punishment;
            }

            public TrackedPlayer Player { get; }
            public Punishment? Punishment { get; }
        }

        /// <summary>Forgets the grace periods waiting: the world is unloading, and each is dropped when it ends.</summary>
        public static void Clear() => AwaitingGrace.Clear();

        /// <summary>A punishment is already on its way for this block, under this owner or the one before.</summary>
        private static bool PunishmentPending(TrackedPBBlock trackedPbBlock) =>
            AwaitingGrace.ContainsKey(trackedPbBlock.ProgrammableBlock.EntityId);

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

        /// <summary>
        /// True when this run over the instant kill run time is not yet the one that kills, see <see cref="TrackedPBBlock.InstantKillStrike"/>;
        /// logged, as a run that long is worth knowing about either way.
        /// </summary>
        private static bool InstantKillSpike(TrackedPlayer player, TrackedPBBlock trackedPbBlock, double lastRunTimeMS)
        {
            int needed = Config.InstantKillStrikes;
            if (trackedPbBlock.InstantKillStrike(needed, Config.InstantKillStrikeWindow)) return false;
            Log.Warn($"Run of {InvariantText.Of(lastRunTimeMS, "0.0000")}ms over the instant kill limit by {player.PlayerName}'s block on grid {trackedPbBlock.CurrentGridName}, "
                     + $"not punished as it may be a spike: the instant kill needs {InvariantText.Of(needed)} such runs within {InvariantText.Of(Config.InstantKillStrikeWindow)} seconds.");
            return true;
        }

        /// <summary>Called after every run of every tracked script. Nothing here may allocate until a limit is broken.</summary>
        /// <param name="collected">A garbage collection happened during the run, see RunClock.</param>
        /// <param name="runTimeLimit">The owner's run-time limit the run was added with, 0 for none.</param>
        /// <param name="spike">The run was a spike, as decided when it was added, see TrackedPBBlock.AddRuntimeData.</param>
        public static void CheckForPunishment(TrackedPlayer player, TrackedPBBlock trackedPbBlock, double lastRunTimeMS, bool collected, double runTimeLimit, bool spike)
        {
            if (!LimiterCore.Active) return;
            if (Config.EnforceOnlyBelowSimThreshold && Sync.ServerSimulationRatio >= Config.EnforceOnlyBelowSimRate) return;
            // Instant-killed already for an earlier run of it queued in the same tick: not punished or told twice. Decided by
            // the batch, not by whether the block works now, so a script that turns itself off after each run is judged.
            if (trackedPbBlock.InstantKill.In(TrackingManager.Batch)) return;

            // The instant kills come first: they are immediate, also while an ordinary punishment waits out its grace period,
            // see PunishPB.
            // InstaKill by run-time
            // Not for a run a collection paused, whose time may be mostly that pause: such a run is left to the run-time and
            // average limits below, which it still counts for in full. The game sometimes times a steady script's run far
            // too long, once: only the InstantKillStrikes'th run over the threshold within the window kills. A run that does
            // not is left to the limits below, which forgive one or two such runs.
            if (!collected && Config.InstantKillThreshold > 0 && lastRunTimeMS > Config.InstantKillThreshold
                && !InstantKillSpike(player, trackedPbBlock, lastRunTimeMS))
            {
                trackedPbBlock.InstantKill.Set(TrackingManager.Batch);
                trackedPbBlock.MarkLastRun(RunOutcome.InstantKilled);
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.CurrentGridName} with a run-time of {InvariantText.Of(lastRunTimeMS, "0.0000")}ms");
                PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Config.InstantKillPunishment.ToPunishment(), $"[{InvariantText.Of(lastRunTimeMS, "0.0000")}ms]");
                return;
            }

            // InstaKill by memory
            if (Config.EnableMemoryMonitoring && Config.PBMemoryThreshold != 0 && Config.PBMemoryThreshold < trackedPbBlock.memoryUsage)
            {
                string usage = trackedPbBlock.MemoryUnmeasured
                    ? "not measurable (counted as over the limit)"
                    : MemoryText.Size(trackedPbBlock.memoryUsage);
                trackedPbBlock.InstantKill.Set(TrackingManager.Batch);
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.CurrentGridName} with memory use: {usage}");
                PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Config.MemoryPunishment.ToPunishment(), $"[{usage}]");
                return;
            }

            if (PunishmentPending(trackedPbBlock)) return;

            PrivilegedPlayer? privilegedPlayer = player.Privileged;
            double allowedRunTime = runTimeLimit;
            double allowedRunTimeAvg = privilegedPlayer?.RuntimeAverageAllowance ?? Config.MaxRunTimeMSAvg;
            int allowedOffences = privilegedPlayer?.OffencesBeforePunishment ?? Config.MaxOffencesBeforePunishment;

            trackedPbBlock.ForgetOffencesOlderThan(OffenceLifetimeMinutes);

            // Check if enough time since last offense has passed.
            if (trackedPbBlock.IsInGraceAfterOffence)
                return;

            // A run over the limit counts unless the window decided, as it was added, that it was a spike: at most 2 of any 50
            // runs, see RunTimeWindow.Add. One offence a run, so the average is not checked as well.
            if (allowedRunTime != 0 && lastRunTimeMS > allowedRunTime)
            {
                if (!spike)
                {
                    trackedPbBlock.MarkLastRun(RunOutcome.Counted);
                    Offence(player, trackedPbBlock, allowedOffences, PunishReason.SingleRuntimeOverLimit, allowedRunTime, lastRunTimeMS, lastRunTimeMS);
                }
                return;
            }

            // Leaves out the spikes the run-time limit forgave, see TrackedPBBlock.RunTimeMSAvg.
            double runTimeMSAvg = trackedPbBlock.RunTimeMSAvg;
            if (allowedRunTimeAvg != 0 && runTimeMSAvg > allowedRunTimeAvg)
                Offence(player, trackedPbBlock, allowedOffences, PunishReason.AverageRuntimeOverLimit, allowedRunTimeAvg, runTimeMSAvg, lastRunTimeMS);
        }

        /// <summary>
        /// Records an offence against the run-time or average limit and warns the owner, sends the graceful shutdown request
        /// with the last offence allowed, and punishes past that.
        /// </summary>
        private static void Offence(TrackedPlayer player, TrackedPBBlock trackedPbBlock, int allowedOffences, PunishReason reason, double limit, double usage, double lastRunTimeMS)
        {
            bool average = reason == PunishReason.AverageRuntimeOverLimit;
            string what = average ? "average-run-time" : "run-time";
            RecordOffence(trackedPbBlock);
            int offences = trackedPbBlock.Offences.Count;
            Log.Info($"PB Block: {InvariantText.Of(trackedPbBlock.ProgrammableBlock.EntityId)} for [{trackedPbBlock.CurrentGridName} >> {trackedPbBlock.ProgrammableBlock.ReadName()}] has exceeded the {what} limit.  Allowed: {InvariantText.Of(limit, "0.0000")}ms  Used: {InvariantText.Of(usage, "0.0000")}ms  Last Run-Time: {InvariantText.Of(lastRunTimeMS, "0.0000")}ms  Offences: {InvariantText.Of(offences)}");

            if (offences <= allowedOffences)
            {
                if (Config.WarnUserOnOffense)
                    Chat.Send($"You have received offence [{InvariantText.Of(offences)} of {InvariantText.Of(allowedOffences)}] for exceeding your {what} limit [{InvariantText.Of(limit, "0.0000")}ms] for [{trackedPbBlock.CurrentGridName} >> {trackedPbBlock.ProgrammableBlock.ReadName()}].  Please reduce the run time of your Programmable Block or it will be punished.", player.PlayerId, Color.Red);

                if (offences == allowedOffences)
                    trackedPbBlock.ProgrammableBlock.Run(average ? "GracefulShutDown::-2" : "GracefulShutDown::-1", UpdateType.Script);
                return;
            }

            PunishPB(player, trackedPbBlock, reason, null, UsageNotes(limit, usage));
        }

        private static void RecordOffence(TrackedPBBlock trackedPbBlock)
        {
            long now = Stopwatch.GetTimestamp();
            trackedPbBlock.Offences.Enqueue(now);
            trackedPbBlock.LastOffenceTick = now;
        }

        private static string UsageNotes(double limit, double usage)
        {
            return $"{Environment.NewLine}Your Limit: [{InvariantText.Of(limit, "0.0000")}ms]{Environment.NewLine}Your Usage: [{InvariantText.Of(usage, "0.0000")}ms]{Environment.NewLine}";
        }

        public static void PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, Punishment? punishment = null, string? notes = null)
        {
            // An instant kill is immediate. It takes the place of an ordinary punishment waiting out its grace period, and the
            // harsher of the two is applied, once: a script told of its grace period can't trade a harsher punishment for
            // the instant kill's, nor be punished twice.
            if (reason == PunishReason.ExtremeUsage)
            {
                Punishment now = punishment ?? player.Privileged?.Punishment ?? Config.Punishment;
                if (AwaitingGrace.TryRemove(trackedPbBlock.ProgrammableBlock.EntityId, out Grace? waiting))
                {
                    Punishment pending = waiting.Punishment ?? waiting.Player.Privileged?.Punishment ?? Config.Punishment;
                    if (Severity(pending) > Severity(now)) now = pending;
                }
                PerformPunish(trackedPbBlock, player, reason, now, notes);
                return;
            }
            if (PunishmentPending(trackedPbBlock)) return; // Already on its way.

            int gracePeriodSeconds = player.Privileged?.GracefulShutDownRequestDelay ?? Config.GracefulShutDownRequestDelay;
            if (gracePeriodSeconds <= 0)
            {
                PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                return;
            }

            Grace grace = new(player, punishment);
            AwaitingGrace[trackedPbBlock.ProgrammableBlock.EntityId] = grace;
            trackedPbBlock.ProgrammableBlock.Run("GracefulShutDown::" + InvariantText.Of(gracePeriodSeconds), UpdateType.Script);
            _ = PunishAfterGraceAsync(trackedPbBlock, player, reason, punishment, notes, gracePeriodSeconds, grace);
        }

        // Task.Delay takes at most int.MaxValue milliseconds, about 24 days.
        private const int MaxGracePeriodSeconds = int.MaxValue / 1000;

        /// <summary>
        /// Punishments are always applied after the delay, whatever the script did with it. If this fails, the block is
        /// released so the next offence is handled again, and the failure is logged.
        /// </summary>
        private static async Task PunishAfterGraceAsync(TrackedPBBlock trackedPbBlock, TrackedPlayer player, PunishReason reason, Punishment? punishment, string? notes, int gracePeriodSeconds, Grace grace)
        {
            long offCount = LimiterCore.OffCount;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(gracePeriodSeconds, MaxGracePeriodSeconds))).ConfigureAwait(false);
                MySandboxGame? game = MySandboxGame.Static;
                if (game is null)
                {
                    EndGrace(trackedPbBlock, grace); // The game has stopped.
                    return;
                }

                game.Invoke(() =>
                {
                    // Taken by an instant kill, which applied the harsher punishment, or dropped when the world unloaded.
                    if (!EndGrace(trackedPbBlock, grace)) return;
                    // Off at any point during the grace period, disabled or the world reloaded, even if on again now: dropped.
                    // A block that changed owner meanwhile is still punished: it is the block that broke the limit, and handing
                    // it to another identity during the grace period must not let it off.
                    if (LimiterCore.GameOnline && LimiterCore.Active && LimiterCore.OffCount == offCount)
                        PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                }, "Advanced_PB_Limiter");
            }
            catch (Exception e)
            {
                EndGrace(trackedPbBlock, grace);
                Log.Error(e, "The grace period before a punishment failed");
            }
        }

        /// <summary>Ends a grace period: false when it was no longer waiting, taken by an instant kill or dropped by a world unload.</summary>
        private static bool EndGrace(TrackedPBBlock trackedPbBlock, Grace grace)
        {
            // Only its own entry, compared by reference: after a world reload the same block may wait out a newer grace period.
            return ((ICollection<KeyValuePair<long, Grace>>)AwaitingGrace).Remove(new KeyValuePair<long, Grace>(trackedPbBlock.ProgrammableBlock.EntityId, grace));
        }

        /// <summary>How harsh a punishment is: Destroy over Damage over Turn Off.</summary>
        private static int Severity(Punishment punishment) => punishment switch
        {
            Punishment.Destroy => 2,
            Punishment.Damage => 1,
            _ => 0
        };

        private static void PerformPunish(TrackedPBBlock trackedPbBlock, TrackedPlayer player, PunishReason reason, Punishment? punishment, string? notes)
        {
            MyProgrammableBlock block = trackedPbBlock.ProgrammableBlock;
            if (block.Closed || block.MarkedForClose) return; // Removed while the script had its grace period.

            Log.Trace($"Punishing PB Block: {InvariantText.Of(block.EntityId)} on grid {trackedPbBlock.CurrentGridName} for {reason.ToString()}. {notes}");
            trackedPbBlock.LastOffenceTick = Stopwatch.GetTimestamp();
            trackedPbBlock.MarkStopped(); // Not counted in the combined run times from here on, unless it runs again.

            try
            {
                Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, notes), player.PlayerId, Color.Red);
                // Handed to someone else during the grace period: they are told too, as it is now their block that stops.
                long owner = block.OwnerId;
                if (owner != 0 && owner != player.PlayerId)
                    Chat.Send($"[{trackedPbBlock.CurrentGridName} >> {block.ReadName()}] was punished for exceeding the limits before it was given to you.", owner, Color.Red);

                switch (punishment ?? player.Privileged?.Punishment ?? Config.Punishment)
                {
                    case Punishment.TurnOff:
                        TurnOff(block);
                        return;

                    case Punishment.Destroy:
                        PerformPunishment_Destroy(block);
                        return;

                    case Punishment.Damage:
                        // 0 uses the server's setting. Resolved here and not when the row is read, so the 0 stays in the
                        // row when CreatePrivilegedUser writes the looked-up player back.
                        PerformPunishment_Damage(block, player.Privileged is { DamageAmount: > 0 } privileged ? privileged.DamageAmount : Config.PunishDamageAmount);
                        // Damage an instant kill took over from a waiting punishment: an instant kill always stops the script.
                        if (reason == PunishReason.ExtremeUsage && !block.Closed && !block.MarkedForClose)
                            TurnOff(block);
                        return;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Error while punishing a programmable block");
            }
        }

        /// <summary>Turns the block off and ends its script, so it runs again only once recompiled. Game thread.</summary>
        internal static void TurnOff(MyProgrammableBlock block)
        {
            block.Enabled = false;
            PbAccess.ClearNeedsInstantiation(block);
            PbAccess.TerminateProgram(block);
            block.Enabled = false;
        }

        private static void PerformPunishment_Destroy(MyProgrammableBlock block)
        {
            TurnOff(block);

            // What one point of damage does to this block once the game has applied its modifiers.
            float scale = ScaledDamage(block, 1) * block.SlimBlock.DamageRatio;
            if (IsProtectedFromDamage(block) || scale <= 0)
            {
                Dismount(block);
                return;
            }

            // Down to 100 integrity, below functional level. Worked out from the integrity the block has left, less damage
            // the game has not applied yet, so a block that has already taken damage stops at 100 as well instead of being
            // removed. A block at or below 100 takes no more. Divided by the modifiers the game multiplies it by again; a mod
            // that changes damage through the game's damage system can still move where it stops.
            float left = block.SlimBlock.Integrity - block.SlimBlock.AccumulatedDamage;
            block.SlimBlock.DoDamage(Math.Max(0, left - 100) / scale, MyDamageType.Unknown, true, null, 0);
        }

        private static void PerformPunishment_Damage(MyProgrammableBlock block, int damageAmount)
        {
            PbAccess.ClearNeedsInstantiation(block);

            // A damage amount of 0 does nothing to any block, so the block is turned off as Damage can't punish it. It is
            // not protected, so "destroy protected PB" does not apply to it.
            if (damageAmount <= 0)
            {
                TurnOff(block);
                return;
            }

            // Protected: the game drops damage here, or a damage modifier of 0 makes it nothing (checked with one point,
            // so the amount itself does not count).
            if (IsProtectedFromDamage(block) || ScaledDamage(block, 1) <= 0)
            {
                // Damage can't land here, so the block is turned off instead: otherwise it keeps running over its limits,
                // and is "punished" again on every offence with nothing happening.
                if (Config.KillProtectedBlock)
                    Dismount(block);
                else
                    TurnOff(block);
                return;
            }

            block.SlimBlock.DoDamage(damageAmount, MyDamageType.Unknown, true, null, 0);
        }

        /// <summary>The damage the block takes from this amount, with the grid's and the block's modifiers, as MySlimBlock.DoDamage works it out.</summary>
        private static float ScaledDamage(MyProgrammableBlock block, float damageAmount)
        {
            MyCubeGrid grid = block.CubeGrid;
            MyCubeGrid root = MyGridPhysicalHierarchy.Static?.GetRoot(grid) ?? grid;
            float gridModifier = Math.Min(grid.GridGeneralDamageModifier.Value, root.GridGeneralDamageModifier.Value);
            return damageAmount * block.SlimBlock.BlockGeneralDamageModifier * gridModifier * block.SlimBlock.BlockDefinition.GeneralDamageMultiplier;
        }

        /// <summary>
        /// True where the game drops damage on this block, asked as MySlimBlock.DoDamage asks it: a grid or world with block
        /// destruction off, or a safe zone, or the world's own setting, that forbids damage at the block's position.
        /// </summary>
        private static bool IsProtectedFromDamage(MyProgrammableBlock block)
        {
            if (!block.CubeGrid.BlocksDestructionEnabled && !block.SlimBlock.ForceBlockDestructible)
                return true;
            return !MySessionComponentSafeZones.IsActionAllowed(block.SlimBlock.WorldPosition, MySafeZoneAction.Damage, 0, 0);
        }

        private static void Dismount(MyProgrammableBlock block)
        {
            block.Enabled = false; // Turn off because it won't need a recompile and restarts when rebuilt by player.
            block.SlimBlock.FullyDismount(new MyInventory());
            PunishReSync(block.EntityId);
        }

        private static string CreateUserMessage(TrackedPlayer player, TrackedPBBlock pbBlock, PunishReason reason, string? message = null )
        {
            bool isNpc = MySession.Static.Players.IdentityIsNpc(player.PlayerId);
            bool isOnline = MySession.Static.Players.IsPlayerOnline(player.PlayerId);
            if (player.SteamId == 0 || isNpc || !isOnline)
            {
                Log.Warn($"No punishment message was sent: SteamID[{player.SteamId}]  IsNPC[{isNpc}]  IsOnline[{isOnline}]  GridName[{pbBlock.CurrentGridName}]");
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

            return $"Your Programmable Block on grid <{pbBlock.CurrentGridName}> {why}{message}";
        }

        /// <summary>
        /// Re-sends the block to every online player. Its replicable is found by entity id: its name is the block's
        /// "MyProgrammableBlock {id} name" (MyEntity.ToString, MyTerminalBlock.ToString), and the block's own name, which
        /// its owner chooses, must not pick up other players' blocks.
        /// </summary>
        private static void PunishReSync(long entityId)
        {
            string prefix = "MyProgrammableBlock {" + entityId.ToString("X8", CultureInfo.InvariantCulture) + "}";
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
                    string instanceName = pair.Key.InstanceName;
                    if (!instanceName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (instanceName.Length > prefix.Length && instanceName[prefix.Length] != ' ') continue;
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
