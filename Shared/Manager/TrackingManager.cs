using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Patches;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Keeps the tracking data. Everything here runs on the game thread from <see cref="Update"/>, once per tick
    /// instead of on timers, so it can use game objects directly. Only the memory check runs on another thread.
    /// </summary>
    public static class TrackingManager
    {
        private const int BLOCK_CLEANUP_TICKS = 5 * 60;
        private const int PLAYER_CLEANUP_TICKS = 60 * 60;
        private const int MEMORY_CHECK_TICKS = 5 * 60;
        private const int COMBINED_CHECK_TICKS = 10 * 60;

        private static IPbLimiterConfig Config => LimiterCore.Config;
        private static readonly LimiterLog Log = new("Tracking Manager");
        private static readonly Random Random = new();

        public static ConcurrentDictionary<long,TrackedPlayer> PlayersTracked { get; } = new ();

        // Runs waiting for the next tick. Filled by PbProfiler while _processing is worked through, then they swap,
        // so no run allocates. The lock is uncontended: scripts run on the game thread, unless a mod misbehaves.
        private static readonly object _pendingLock = new();
        private static TrackingDataUpdateRequest[] _pending = new TrackingDataUpdateRequest[256];
        private static TrackingDataUpdateRequest[] _processing = new TrackingDataUpdateRequest[256];
        private static int _pendingCount;

        private static int _memoryCheckRunning;

        public static void Update(long tick)
        {
            ProcessUpdateTrackingData();

            if (tick % BLOCK_CLEANUP_TICKS == 0)
                CleanUpOldBlocks();

            if (tick % PLAYER_CLEANUP_TICKS == 0)
                CleanUpOldPlayers();

            if (tick % COMBINED_CHECK_TICKS == 0)
                CheckAllUserBlocksForCombinedLimits();

            if (tick % MEMORY_CHECK_TICKS == 0)
                StartMemoryCheck();
        }

        public static void Stop()
        {
            lock (_pendingLock)
            {
                Array.Clear(_pending, 0, _pendingCount);
                _pendingCount = 0;
            }
            PlayersTracked.Clear();
        }

        public static List<TrackedPlayer> GetTrackedPlayerData()
        {
            return PlayersTracked.Values.ToList();
        }

        public static TrackedPlayer? GetTrackedPlayerDataById(long playerId)
        {
            return PlayersTracked.TryGetValue(playerId, out TrackedPlayer? player)
                ? player
                : null;
        }

        /// <summary>
        /// Turns off one tracked block, on the game thread. Only tracked blocks can be reached,
        /// so this cannot be pointed at any other entity. Returns how many were turned off.
        /// </summary>
        public static int TurnOffBlock(long entityId)
        {
            foreach (KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
            {
                TrackedPBBlock? block = trackedPlayer.Value.GetTrackedPB(entityId);
                if (block is null) continue;

                Log.Info($"Request to turn off the programmable block [{block.ProgrammableBlock.CustomName}] from Quasar.");
                return TurnOff(block) ? 1 : 0;
            }

            return 0;
        }

        /// <summary>Turns off all of a player's tracked blocks, on the game thread. Returns how many were turned off.</summary>
        public static int TurnOffPlayer(long identityId)
        {
            if (!PlayersTracked.TryGetValue(identityId, out TrackedPlayer? player)) return 0;

            Log.Info($"Request to turn off ALL the programmable blocks for [{player.PlayerName}] from Quasar.");
            int turnedOff = 0;
            foreach (TrackedPBBlock block in player.GetAllPBBlocks)
            {
                if (TurnOff(block))
                    turnedOff++;
            }
            return turnedOff;
        }

        private static bool TurnOff(TrackedPBBlock trackedBlock)
        {
            MyProgrammableBlock block = trackedBlock.ProgrammableBlock;
            if (block.Closed || block.MarkedForClose || !block.Enabled) return false;

            block.Enabled = false;
            return true;
        }

        private static void CleanUpOldBlocks()
        {
            foreach (KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
            {
                trackedPlayer.Value.CleanUpOldPBBlocks();
                trackedPlayer.Value.RefreshPrivilegedPlayer();
            }
        }

        private static void CleanUpOldPlayers()
        {
            int keepMinutes = Config.RemovePlayersWithNoPBFrequencyInMinutes;
            if (keepMinutes <= 0) return;

            foreach (KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
            {
                if (trackedPlayer.Value.PBBlockCount > 0) continue;
                if (trackedPlayer.Value.LastDataUpdateTick.TicksTillNow_TimeSpan().TotalMinutes < keepMinutes) continue;
                PlayersTracked.TryRemove(trackedPlayer.Key, out _);
            }
        }

        public static void UpdateTrackingData(MyProgrammableBlock pb, double runTime)
        {
            lock (_pendingLock)
            {
                if (_pendingCount == _pending.Length)
                    Array.Resize(ref _pending, _pending.Length * 2);

                _pending[_pendingCount++] = new TrackingDataUpdateRequest(pb, runTime);
            }
        }

        private static void ProcessUpdateTrackingData()
        {
            TrackingDataUpdateRequest[] batch;
            int count;
            lock (_pendingLock)
            {
                if (_pendingCount == 0) return;
                batch = _pending;
                count = _pendingCount;
                _pending = _processing;
                _processing = batch;
                _pendingCount = 0;
            }

            try
            {
                for (int index = 0; index < count; index++)
                    ProcessRun(in batch[index]);
            }
            finally
            {
                Array.Clear(batch, 0, count); // Must not keep the blocks alive.
            }
        }

        private static void ProcessRun(in TrackingDataUpdateRequest data)
        {
            long trackTime = Config.ShowTimingsBurp ? Stopwatch.GetTimestamp() : 0;

            MyProgrammableBlock block = data.ProgBlock;
            if (block.Closed || block.MarkedForClose) return;

            long ownerId = block.OwnerId;
            if (ownerId == 0)
            {
                if (Config.TurnOffUnownedBlocks && block.Enabled)
                    block.Enabled = false;
                return; // Track un-owned blocks? something to think about...
            }

            if (Config.IgnoreNPCs && MySession.Static.Players.IdentityIsNpc(ownerId)) return;

            if (!PlayersTracked.TryGetValue(ownerId, out TrackedPlayer? player))
            {
                player = new TrackedPlayer(ownerId);
                PlayersTracked[ownerId] = player;
            }

            player.UpdatePBBlockData(in data);

            if (Config.ShowTimingsBurp)
                Log.Info($"[{LimiterCore.GetThreadName()} THREAD] Processed PB Tracking Data[{block.CustomName}] in: {trackTime.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
        }

        public static void PBRecompiled(MyProgrammableBlock? pb)
        {
            if (!Config.Enabled) return;
            if (pb is null) return;

            if (!PlayersTracked.TryGetValue(pb.OwnerId, out TrackedPlayer? player)) return;

            if (Config.ClearHistoryOnRecompile)
                player.ResetPBBlockData(pb);
        }

        private static void CheckAllUserBlocksForCombinedLimits()
        {
            if (!Config.Enabled) return;
            if (!Config.CheckAllUserBlocksCombined) return;
            if (Config.EnforceOnlyBelowSimThreshold && Sync.ServerSimulationRatio >= Config.EnforceOnlyBelowSimRate) return;

            foreach (KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
            {
                long trackStart = Stopwatch.GetTimestamp();
                TrackedPlayer player = trackedPlayer.Value;

                // clear old combined warnings
                for (int index = player.CombinedOffences.Count - 1; index >= 0; index--)
                {
                    if (player.CombinedOffences[index].TicksTillNow_TimeSpan().TotalMinutes >= PunishmentManager.OffenceLifetimeMinutes)
                        player.CombinedOffences.RemoveAt(index);
                }

                PrivilegedPlayer? privilegedPlayer = player.Privileged;
                if (privilegedPlayer is { NoCombinedLimits: true }) continue;

                double combinedRuntimeAllowance = privilegedPlayer?.CombinedRuntimeAllowance ?? Config.MaxAllBlocksCombinedRunTimeMS;
                double combinedRuntimeAverageAllowance = privilegedPlayer?.CombinedRuntimeAverageAllowance ?? Config.MaxAllBlocksCombinedRunTimeMSAvg;

                player.GetCombinedRuntimes(out double totalMS, out double totalMSAvg);

                // 0 means no limit. It is also what a privileged player has when these were left blank.
                if (combinedRuntimeAllowance > 0 && totalMS > combinedRuntimeAllowance)
                    HandleCombinedOffence(player, PunishmentManager.PunishReason.CombinedRuntimeOverLimit);
                else if (combinedRuntimeAverageAllowance > 0 && totalMSAvg > combinedRuntimeAverageAllowance)
                    HandleCombinedOffence(player, PunishmentManager.PunishReason.CombinedAverageRuntimeOverLimit);

                if (Config.ShowTimingsBurp)
                    Log.Info($"[{LimiterCore.GetThreadName()} THREAD] Checked User Combined Usage[{player.PlayerName}] in: {trackStart.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
            }
        }

        private static void HandleCombinedOffence(TrackedPlayer player, PunishmentManager.PunishReason reason)
        {
            player.CombinedOffences.Add(Stopwatch.GetTimestamp());

            // Only the blocks that make up the combined total are warned or punished. A block that is already off, damaged or
            // unpowered stays tracked for a while, and punishing it again would only take more of it. A block in its startup
            // time has its own runs ignored, so it is not punished for the player's other blocks either.
            List<TrackedPBBlock> blocks = player.GetAllPBBlocks;
            blocks.RemoveAll(block => !block.IsRunning || block.IsUnderGracePeriod(player.Privileged));
            if (blocks.Count == 0) return;

            if (player.CombinedOffences.Count <= Config.MaxCombinedOffencesBeforePunishment)
            {
                foreach (TrackedPBBlock block in blocks)
                    block.ProgrammableBlock.Run("GracefulShutDown::-3", UpdateType.Script);
                return;
            }

            if (Config.PunishAllUserBlocksCombinedOnExcessLimits)
            {
                foreach (TrackedPBBlock block in blocks)
                {
                    if (!block.IsInGraceAfterOffence)
                        PunishmentManager.PunishPB(player, block, reason);
                }
            }
            else
            {
                TrackedPBBlock block = blocks[Random.Next(blocks.Count)];
                if (!block.IsInGraceAfterOffence)
                    PunishmentManager.PunishPB(player, block, reason);
            }
        }

        private static void StartMemoryCheck()
        {
            if (!Config.EnableMemoryMonitoring) return;
            if (Interlocked.CompareExchange(ref _memoryCheckRunning, 1, 0) != 0) return;

            Task.Run(CheckInstanceMemoryUsages);
        }

        /// <summary>Runs off the game thread, a script can hold a lot of objects. Safe while scripts run, see <see cref="GetSize"/>.</summary>
        private static void CheckInstanceMemoryUsages()
        {
            try
            {
                foreach (KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
                {
                    foreach (TrackedPBBlock block in trackedPlayer.Value.GetAllPBBlocks)
                    {
                        IMyGridProgram? program = PbAccess.GetProgram(block.ProgrammableBlock);
                        if (program is null)
                        {
                            block.ResetMemoryUsage();
                            continue;
                        }

                        long size = GetSize.OfProgram(program, Config.PBMemoryThreshold, out long time);
                        block.updateMemoryUsage(size);

                        if (Config.ShowTimingsBurp)
                            Log.Info($"[{LimiterCore.GetThreadName()} THREAD] Programmable Block=>[{block.ProgrammableBlock.EntityId}] Size=>[{size} bytes] ScanTime=>[{time.TimeElapsed(0).TotalMilliseconds}ms]");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "The memory check failed");
            }
            finally
            {
                Volatile.Write(ref _memoryCheckRunning, 0);
            }
        }
    }
}
