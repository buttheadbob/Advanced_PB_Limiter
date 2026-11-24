#nullable enable
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
    /// Keeps the tracking data. It runs on the game thread from <see cref="Update"/>, once per tick instead of on timers, so
    /// it can use game objects directly. The memory check runs on another thread, and PBRecompiled on whichever thread
    /// compiles the script, which can be another one; Stop runs on the reload thread for a Torch reload.
    /// </summary>
    public static class TrackingManager
    {
        private const int BLOCK_CLEANUP_TICKS = 5 * 60;
        private const int PLAYER_CLEANUP_TICKS = 60 * 60;
        private const int MEMORY_CHECK_TICKS = 5 * 60;
        /// <summary>How often the combined limits are checked, in game ticks.</summary>
        internal const int COMBINED_CHECK_TICKS = 10 * 60;

        private static IPbLimiterConfig Config => LimiterCore.Config;
        private static readonly LimiterLog Log = new("Tracking Manager");
        private static readonly Random Random = new();

        public static ConcurrentDictionary<long,TrackedPlayer> PlayersTracked { get; } = new ();

        // The same players as PlayersTracked, for the periodic jobs on the game thread, which walk it with an index so
        // they allocate nothing. Only the game thread changes the list. Stop, which a Torch plugin reload runs on its own
        // thread, puts a new empty list in its place instead of clearing it, so a job on the game thread walking the old one
        // is never cut short; each job reads the field once.
        private static volatile List<TrackedPlayer> _gameThreadPlayers = new();

        private static readonly ThreadStart MemoryCheck = CheckInstanceMemoryUsages;

        // Set once the memory check's thread failed to start, so that is logged once.
        private static int _memoryCheckStartFailed;

        // Set once the memory check was found unable to run on this runtime, so that is logged once.
        private static int _memoryCheckUnavailable;

        // Runs waiting for the next tick. Filled by PbProfiler while _processing is worked through, then they swap,
        // so no run allocates. The lock is uncontended: scripts run on the game thread, unless a mod misbehaves.
        private static readonly object _pendingLock = new();
        private static TrackingDataUpdateRequest[] _pending = new TrackingDataUpdateRequest[256];
        private static TrackingDataUpdateRequest[] _processing = new TrackingDataUpdateRequest[256];
        private static int _pendingCount;

        private static int _memoryCheckRunning;

        // Blocks recompiled by an owner that had not taken up their tracking yet, while it was still tracked under the owner
        // before, by entity id, with that owner: the reset waits for that owner's take-up, see PBRecompiled. Dropped
        // when taken, when the block closes or passes to yet another owner, and at Stop. Any thread: a compile can come off
        // the game thread.
        private static readonly ConcurrentDictionary<long, ResetMark> _resetOnTakeUp = new();

        // Held around a take-up and around a recompile's look for a block between owners, so the recompile sees the block
        // either before it is taken, still tracked elsewhere, or after, tracked by its owner: never in neither place.
        private static readonly object _takeUpLock = new();

        // Blocks given to nobody or to an ignored NPC, kept with their history for the tracked owner they go to next:
        // otherwise handing a block to nobody and back would clear its offences and give it a fresh startup allowance.
        // Forgotten when the block is removed, or when it has not run for the player keep time. Changed under _takeUpLock.
        private static readonly ConcurrentDictionary<long, TrackedPBBlock> _ownerless = new();

        private readonly struct ResetMark
        {
            public ResetMark(MyProgrammableBlock block, long ownerId)
            {
                Block = block;
                OwnerId = ownerId;
            }

            public MyProgrammableBlock Block { get; }
            public long OwnerId { get; }
        }

        /// <summary>
        /// Moves a block whose owner changed to its new owner's tracking, resting with its history until it runs there, so
        /// reports, turning off, the shutdown warning and the memory check see it under that owner at once: handing a block
        /// to another identity must not give it a fresh startup allowance or clear its offences. A block given to an owner
        /// the limiter does not track, nobody or an ignored NPC, is held with its history for the next tracked owner. Game
        /// thread, from a cleanup pass.
        /// </summary>
        internal static void HandOver(TrackedPlayer from, long entityId, TrackedPBBlock block)
        {
            MyProgrammableBlock pb = block.ProgrammableBlock;
            long ownerId = pb.OwnerId;
            bool tracked = ownerId != 0 && !(Config.IgnoreNPCs && MySession.Static.Players.IdentityIsNpc(ownerId));
            // Under the lock a recompile's look for a block between owners takes, so that look finds it under one owner or
            // the other, never in neither. Added to the new owner before it leaves the old one.
            lock (_takeUpLock)
            {
                TrackedPlayer? to = tracked ? PlayerFor(ownerId) : null;
                if (to != null)
                    to.Rest(entityId, block);
                else
                    _ownerless[entityId] = block;
                from.Forget(entityId, block);
                // A recompile by the new owner while the block was still under the old one starts it over now, held for
                // nobody as well as tracked.
                bool marked = _resetOnTakeUp.TryRemove(entityId, out ResetMark mark);
                if (marked && mark.OwnerId == ownerId && LimiterCore.Active && Config.ClearHistoryOnRecompile)
                {
                    if (to != null)
                        to.StartOver(pb);
                    else
                        block.ClearRunTimes();
                }
            }
        }

        /// <summary>The tracked player for an owner, created and listed on first sight. Game thread.</summary>
        private static TrackedPlayer PlayerFor(long ownerId)
        {
            if (!PlayersTracked.TryGetValue(ownerId, out TrackedPlayer? player))
            {
                player = new TrackedPlayer(ownerId);
                PlayersTracked[ownerId] = player;
                _gameThreadPlayers.Add(player);
            }
            return player;
        }

        /// <summary>
        /// The tracking of a block still listed under the player who owned it before, for <paramref name="newOwnerId"/> to
        /// take up when its first run comes before the cleanup that hands it over. Null when nobody else has it. Game thread,
        /// once per block per owner, never per run.
        /// </summary>
        internal static TrackedPBBlock? TakeHandedOver(long entityId, long newOwnerId)
        {
            // Callers hold _takeUpLock, see TakeUp.
            TrackedPBBlock? block;
            List<TrackedPlayer> players = _gameThreadPlayers;
            for (int index = 0; index < players.Count; index++)
            {
                if (players[index].PlayerId != newOwnerId && players[index].TryGiveUp(entityId, out block))
                    return block;
            }
            // Or held after it was given to nobody, or to an ignored NPC.
            return _ownerless.TryRemove(entityId, out block) ? block : null;
        }

        /// <summary>Forgets reset marks that can no longer apply. Game thread.</summary>
        private static void ForgetStaleHandOvers()
        {
            if (!_resetOnTakeUp.IsEmpty)
            {
                foreach (KeyValuePair<long, ResetMark> kvp in _resetOnTakeUp)
                {
                    MyProgrammableBlock block = kvp.Value.Block;
                    // Closed, or passed on to yet another owner: the recompile it waits to apply was for a take-up that
                    // will not come.
                    if (block.Closed || block.MarkedForClose || block.OwnerId != kvp.Value.OwnerId)
                        _resetOnTakeUp.TryRemove(kvp.Key, out _);
                }
            }

            if (!_ownerless.IsEmpty)
            {
                int keepMinutes = Config.RemovePlayersWithNoPBFrequencyInMinutes;
                foreach (KeyValuePair<long, TrackedPBBlock> kvp in _ownerless)
                {
                    MyProgrammableBlock block = kvp.Value.ProgrammableBlock;
                    bool stale = block.Closed || block.MarkedForClose
                                 || (keepMinutes > 0 && kvp.Value.LastUpdateTick.TicksTillNow_TimeSpan().TotalMinutes >= keepMinutes);
                    if (!stale) continue;
                    lock (_takeUpLock)
                        _ownerless.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>The game tick the limiter is at, as its Update was last given it. Read from any thread.</summary>
        internal static long CurrentTick => Interlocked.Read(ref _currentTick);
        private static long _currentTick;

        public static void Update(long tick)
        {
            Interlocked.Exchange(ref _currentTick, tick);
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
            ClearShared();
            _gameThreadPlayers = new List<TrackedPlayer>();
        }

        /// <summary>What Stop and ClearAfterStop both clear: everything but the game thread's list, which they treat apart.</summary>
        private static void ClearShared()
        {
            lock (_pendingLock)
            {
                Array.Clear(_pending, 0, _pendingCount);
                _pendingCount = 0;
            }
            PlayersTracked.Clear();
            _resetOnTakeUp.Clear();
            _ownerless.Clear();
        }

        /// <summary>
        /// Game thread, after a tick's jobs, when the world went offline during the tick: clears what the tick added after
        /// Stop ran on another thread. Clears the game thread's list in place, as no job is walking it now and nothing is
        /// allocated; a Stop running meanwhile swaps in an empty one either way.
        /// </summary>
        public static void ClearAfterStop()
        {
            ClearShared();
            _gameThreadPlayers.Clear();
        }

        public static List<TrackedPlayer> GetTrackedPlayerData()
        {
            List<TrackedPlayer> players = new(PlayersTracked.Count);
            CopyTrackedPlayers(players);
            return players;
        }

        /// <summary>
        /// Fills <paramref name="target"/> with the players tracked now, clearing it first. Safe while the game thread
        /// adds or removes them. The live view reuses one list instead of allocating and copying one per refresh.
        /// </summary>
        internal static void CopyTrackedPlayers(List<TrackedPlayer> target)
        {
            target.Clear();
            foreach (KeyValuePair<long, TrackedPlayer> pair in PlayersTracked)
                target.Add(pair.Value);
        }

        /// <summary>Notes that the limiter or an admin turned the block off now, if its owner tracks it, see TrackedPBBlock.MarkStopped. Game thread.</summary>
        internal static void MarkStopped(MyProgrammableBlock pb) => GetTrackedPlayerDataById(pb.OwnerId)?.GetTrackedPB(pb.EntityId)?.MarkStopped();

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

                Log.Info($"Request to turn off the programmable block [{block.ProgrammableBlock.ReadName()}] from Quasar.");
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
            foreach (TrackedPBBlock block in player.GetAllPBBlocks())
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
            trackedBlock.MarkStopped();
            return true;
        }

        private static void CleanUpOldBlocks()
        {
            List<TrackedPlayer> players = _gameThreadPlayers;
            for (int index = 0; index < players.Count; index++)
            {
                players[index].CleanUpOldPBBlocks();
                players[index].RefreshPrivilegedPlayer();
            }
            ForgetStaleHandOvers();
        }

        private static void CleanUpOldPlayers()
        {
            int keepMinutes = Config.RemovePlayersWithNoPBFrequencyInMinutes;
            if (keepMinutes <= 0) return;

            List<TrackedPlayer> players = _gameThreadPlayers;
            for (int index = players.Count - 1; index >= 0; index--)
            {
                TrackedPlayer player = players[index];
                if (!player.HasNoBlocks) continue;
                if (player.LastDataUpdateTick.TicksTillNow_TimeSpan().TotalMinutes < keepMinutes) continue;
                PlayersTracked.TryRemove(player.PlayerId, out _);
                players.RemoveAt(index);
            }
        }

        public static void UpdateTrackingData(MyProgrammableBlock pb, double runTime, bool collected)
        {
            lock (_pendingLock)
            {
                // Checked under the lock Stop clears the queue with: a run that passed the caller's checks before the world
                // unloaded or a Torch reload retired this instance is either cleared by Stop or not queued, so it never keeps a
                // block and its grid alive in a retired assembly. GameOnline is cleared before Stop takes the lock.
                if (!LimiterCore.GameOnline) return;
                if (_pendingCount == _pending.Length)
                    Array.Resize(ref _pending, _pending.Length * 2);

                _pending[_pendingCount++] = new TrackingDataUpdateRequest(pb, runTime, collected);
            }
        }

        /// <summary>Counts the batches of queued runs processed, one per tick with runs. Game thread.</summary>
        internal static long Batch { get; private set; }

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

            Batch++;
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
                // Not tracked, as no player answers for it: turned off with its script ended, so another block can't turn it
                // on and run it unjudged.
                if (Config.TurnOffUnownedBlocks)
                    PunishmentManager.TurnOff(block);
                return;
            }

            if (Config.IgnoreNPCs && MySession.Static.Players.IdentityIsNpc(ownerId)) return;

            TrackedPlayer player = PlayerFor(ownerId);

            player.RecordRun(in data);

            if (Config.ShowTimingsBurp)
                Log.Info($"[{LimiterCore.GetThreadName()} THREAD] Processed PB Tracking Data[{block.ReadName()}] in: {trackTime.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
        }

        public static void PBRecompiled(MyProgrammableBlock? pb)
        {
            if (!LimiterCore.Active) return;
            if (pb is null) return;

            bool clearHistory = Config.ClearHistoryOnRecompile;
            if (PlayersTracked.TryGetValue(pb.OwnerId, out TrackedPlayer? player) && player.Recompiled(pb, clearHistory)) return;

            // Given to this owner and not handed over yet: its tracking is still under the owner before, until the next
            // cleanup moves it here. In survival the game ends a script when its block changes owner, so the new owner's first step
            // is this recompile, which starts the block over as any recompile does; in creative the script keeps running
            // and this is an ordinary recompile. The reset is left for this owner's take-up or hand-over, under the lock both
            // hold, so the block is found in one place or the other; it is counted where it is now. A block tracked nowhere
            // gets no mark: there is nothing to reset, and its first run starts it fresh.
            lock (_takeUpLock)
            {
                if (PlayersTracked.TryGetValue(pb.OwnerId, out player) && player.Recompiled(pb, clearHistory)) return;
                TrackedPlayer? elsewhere = TrackedElsewhere(pb.EntityId, pb.OwnerId);
                if (elsewhere is null)
                {
                    // Held after it was given to nobody or an ignored NPC: it starts over here, as no take-up for its owner
                    // will come to apply a mark.
                    if (!_ownerless.TryGetValue(pb.EntityId, out TrackedPBBlock? held)) return;
                    held.CountRecompile();
                    if (clearHistory)
                        held.ClearRunTimes();
                    return;
                }
                elsewhere.Recompiled(pb, false);
                if (clearHistory)
                    _resetOnTakeUp[pb.EntityId] = new ResetMark(pb, pb.OwnerId);
            }
        }

        /// <summary>
        /// After a compile, also with the limiter off: the new script, or none when it failed, has its memory use measured
        /// afresh, so the old script's figure can't instant-kill it on its first run. Done after the compile, when the old
        /// script is gone, so a memory pass can't measure it again; one that read it before is ignored, see
        /// TrackedPBBlock.MemoryResets. From whichever thread compiles the script.
        /// </summary>
        internal static void PBCompiled(MyProgrammableBlock pb)
        {
            long entityId = pb.EntityId;
            if (PlayersTracked.TryGetValue(pb.OwnerId, out TrackedPlayer? owner) && owner.GetTrackedPB(entityId) is { } tracked)
            {
                tracked.ResetMemoryUsage();
                return;
            }
            // Still under the owner before, or held after it was given to nobody.
            foreach (KeyValuePair<long, TrackedPlayer> other in PlayersTracked)
            {
                if (other.Value.GetTrackedPB(entityId) is { } elsewhere)
                {
                    elsewhere.ResetMemoryUsage();
                    return;
                }
            }
            if (_ownerless.TryGetValue(entityId, out TrackedPBBlock? held))
                held.ResetMemoryUsage();
        }

        /// <summary>The player other than its owner whose tracking has the block, or null. Any thread.</summary>
        private static TrackedPlayer? TrackedElsewhere(long entityId, long ownerId)
        {
            foreach (KeyValuePair<long, TrackedPlayer> other in PlayersTracked)
            {
                if (other.Key != ownerId && other.Value.GetTrackedPB(entityId) is not null)
                    return other.Value;
            }
            return null;
        }

        /// <summary>
        /// Takes up a block still listed under its previous owner for <paramref name="player"/> and lists it there, as one
        /// step against a recompile, then starts it over if that player recompiled it while it was between owners and
        /// recompiles still clear the history. Null when nobody else has it. Game thread, once per block per owner.
        /// </summary>
        internal static TrackedPBBlock? TakeUp(TrackedPlayer player, MyProgrammableBlock pb)
        {
            TrackedPBBlock? taken;
            bool reset;
            lock (_takeUpLock)
            {
                taken = TakeHandedOver(pb.EntityId, player.PlayerId);
                if (taken is null)
                {
                    // Nobody else has it: the block starts fresh here, and a mark left for it must not apply to a later
                    // take-up.
                    _resetOnTakeUp.TryRemove(pb.EntityId, out _);
                    return null;
                }
                player.List(pb.EntityId, taken);
                // A mark for another owner is stale: that owner's recompile was for a hand-over that never reached them.
                reset = _resetOnTakeUp.TryRemove(pb.EntityId, out ResetMark mark) && mark.OwnerId == player.PlayerId;
            }
            if (reset && LimiterCore.Active && Config.ClearHistoryOnRecompile)
                player.StartOver(pb);
            return taken;
        }

        private static void CheckAllUserBlocksForCombinedLimits()
        {
            if (!LimiterCore.Active) return;
            if (!Config.CheckAllUserBlocksCombined) return;
            if (Config.EnforceOnlyBelowSimThreshold && Sync.ServerSimulationRatio >= Config.EnforceOnlyBelowSimRate) return;

            List<TrackedPlayer> players = _gameThreadPlayers;
            for (int playerIndex = 0; playerIndex < players.Count; playerIndex++)
            {
                long trackStart = Stopwatch.GetTimestamp();
                TrackedPlayer player = players[playerIndex];

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

                CombinedRunTimes combined = player.Combined();
                double totalMS = combined.LastRunTimeMS;
                double totalMSAvg = combined.RunTimeMSAvg;

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
            List<TrackedPBBlock> blocks = player.GetPBBlocks();
            blocks.RemoveAll(block => !player.CountsInCombined(block, false));
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
            if (!LimiterCore.Active || !Config.EnableMemoryMonitoring) return;
            if (!GetSize.Available)
            {
                if (Interlocked.Exchange(ref _memoryCheckUnavailable, 1) == 0)
                {
                    const string unavailable = "The memory check can't read script fields on this server's .NET runtime, so script memory is not measured and the memory limit is not applied. Logged once.";
                    if (GetSize.UnavailableBecause is { } failure)
                        Log.Error(failure, unavailable);
                    else
                        Log.Warn(unavailable);
                }
                return;
            }
            if (Interlocked.CompareExchange(ref _memoryCheckRunning, 1, 0) != 0) return;

            // Its own thread, with a stack no script can exhaust while the runtime loads its types, see GetSize.ScanThread.
            try
            {
                GetSize.ScanThread(MemoryCheck).Start();
            }
            catch (Exception e)
            {
                // No thread to be had now, whatever the reason: the next pass tries again.
                Volatile.Write(ref _memoryCheckRunning, 0);
                if (Interlocked.Exchange(ref _memoryCheckStartFailed, 1) == 0)
                    Log.Error(e, "The memory check could not start its thread; every block keeps its last figure, and it tries again on the next pass. Logged once.");
            }
        }

        /// <summary>Runs off the game thread, a script can hold a lot of objects. Safe while scripts run, see <see cref="GetSize"/>.</summary>
        private static void CheckInstanceMemoryUsages()
        {
            try
            {
                foreach (KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
                {
                    foreach (TrackedPBBlock block in trackedPlayer.Value.GetAllPBBlocks())
                    {
                        // Taken before the script is read: a recompile meanwhile resets the figure, and this size is then the
                        // old script's.
                        int resets = block.MemoryResets;
                        IMyGridProgram? program = PbAccess.GetProgram(block.ProgrammableBlock);
                        if (program is null)
                        {
                            block.ResetMemoryUsage();
                            continue;
                        }

                        long limit = Config.PBMemoryThreshold;
                        long size;
                        long time = 0;
                        string? why;
                        try
                        {
                            size = ProgramSize.Of(program, limit, out time, out why);
                        }
                        catch (OutOfMemoryException)
                        {
                            // The server is short of memory, not the script's doing: its last figure stands, and the next pass
                            // tries again.
                            if (block.MemoryScanFailed())
                                Log.Warn($"The server ran out of memory while the memory check measured [{block.GridName} >> {block.BlockName}]; it keeps its last figure and is measured again on the next pass. Logged once for this block.");
                            continue;
                        }
                        catch (Exception e)
                        {
                            // Not a shape the check knows to be unmeasurable, so not held against the script: its last figure
                            // stands, and it never stops the check for the other blocks.
                            if (block.MemoryScanFailed())
                                Log.Error(e, $"The memory check failed on [{block.GridName} >> {block.BlockName}]; it keeps its last figure and is measured again on the next pass. Logged once for this block.");
                            continue;
                        }
                        if (why is not null && block.MemoryScanFailed())
                            Log.Warn($"The memory check could not measure [{block.GridName} >> {block.BlockName}] ({why}), so it {(limit > 0 ? "counts as over the memory limit" : "is shown as not measurable")}. Logged once for this block.");
                        block.updateMemoryUsage(size, resets, why is not null);

                        if (Config.ShowTimingsBurp)
                            Log.Info($"[{LimiterCore.GetThreadName()} THREAD] Programmable Block=>[{block.ProgrammableBlock.EntityId.ToString(CultureInfo.InvariantCulture)}] Size=>[{size.ToString(CultureInfo.InvariantCulture)} bytes] ScanTime=>[{time.TimeElapsed(0).TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}ms]");
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
