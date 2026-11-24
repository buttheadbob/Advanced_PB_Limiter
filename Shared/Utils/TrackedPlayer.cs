#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Advanced_PB_Limiter.Manager;
using ExtensionMethods;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>A player and their programmable blocks. Updated on the game thread, the memory check and reports read it from others.</summary>
    public sealed class TrackedPlayer
    {
        private static IPbLimiterConfig Config => LimiterCore.Config;
        private static readonly LimiterLog Log = new("Tracked Player");

        private readonly ConcurrentDictionary<long, TrackedPBBlock> PBBlocks = new();

        // Blocks dropped for being idle, by entity id, kept to be tracked again as they were, in the startup time they
        // started. A script run less often than RemoveInactivePBsAfterSeconds is dropped between runs; tracked again as
        // new, in a new startup time, its runs would never be checked. Concurrent: a compile can reset it off the game thread.
        private readonly ConcurrentDictionary<long, TrackedPBBlock> _resting = new();

        // Held while a block moves between PBBlocks and _resting, and while both are copied, so a reader off the game thread
        // never sees a block in both or in neither. Moves are rare: a cleanup pass, a resting block's next run, a recompile,
        // which can run off the game thread. A move adds to the new place before it removes from the old. GetTrackedPB and
        // HasNoBlocks read PBBlocks without the lock, the common case, and take it only when that does not settle it.
        private readonly object _moves = new();

        public string? PlayerName { get; }
        public long PlayerId { get; }
        public ulong SteamId { get; }
        public bool IsNpc { get; }
        public long LastDataUpdateTick { get; private set; }
        public List<long> CombinedOffences { get; } = new();

        // Written on the game thread by RefreshPrivilegedPlayer, read by reports on other threads.
        private PrivilegedPlayer? _privileged;

        /// <summary>Looked up once in a while rather than on every run, see <see cref="RefreshPrivilegedPlayer"/>.</summary>
        public PrivilegedPlayer? Privileged => Volatile.Read(ref _privileged);

        public TrackedPlayer(long playerId)
        {
            PlayerId = playerId;
            string? name = MySession.Static.Players.TryGetIdentity(playerId)?.DisplayName;
            PlayerName = name?.SafeName();
            SteamId = MySession.Static.Players.TryGetSteamId(playerId);
            IsNpc = MySession.Static.Players.IdentityIsNpc(playerId);
            LastDataUpdateTick = Stopwatch.GetTimestamp();
            RefreshPrivilegedPlayer();
        }

        public void RefreshPrivilegedPlayer()
        {
            Volatile.Write(ref _privileged, Config.TryGetPrivilegedPlayer(SteamId, out PrivilegedPlayer? privilegedPlayer) ? privilegedPlayer : null);
        }

        /// <summary>No block tracked or waiting to be tracked again. Game thread.</summary>
        public bool HasNoBlocks
        {
            get
            {
                if (!PBBlocks.IsEmpty) return false;
                lock (_moves)
                    return PBBlocks.IsEmpty && _resting.IsEmpty; // A block moving to PBBlocks off the game thread is seen.
            }
        }

        /// <summary>A tracked block, or one resting after it went idle, whose script may still run.</summary>
        public TrackedPBBlock? GetTrackedPB(long entityId)
        {
            if (PBBlocks.TryGetValue(entityId, out TrackedPBBlock? trackedPBBlock)) return trackedPBBlock;
            lock (_moves) // A block moving between the two is in one of them once the move is done.
            {
                if (PBBlocks.TryGetValue(entityId, out trackedPBBlock)) return trackedPBBlock;
                return _resting.TryGetValue(entityId, out TrackedPBBlock? resting) ? resting : null;
            }
        }

        public void RecordRun(in TrackingDataUpdateRequest data)
        {
            long now = Stopwatch.GetTimestamp();
            LastDataUpdateTick = now;

            long entityId = data.ProgBlock.EntityId;
            if (!PBBlocks.TryGetValue(entityId, out TrackedPBBlock? trackedPBBlock))
            {
                trackedPBBlock = StartTracking(entityId, data.ProgBlock);
                if (trackedPBBlock is null) return;
            }

            trackedPBBlock.LastUpdateTick = now;
            trackedPBBlock.MarkRun();
            if (trackedPBBlock.IsUnderGracePeriod(Privileged)) return;

            trackedPBBlock.AddRuntimeData(data.RunTime);
            PunishmentManager.CheckForPunishment(this, trackedPBBlock, data.RunTime, data.Collected);
        }

        /// <summary>
        /// The first run of a block: it is tracked from the next run on, and null is returned. Once per block, not per run.
        /// A block dropped for being idle comes back as it was, with its runs, offences and startup time, and this run
        /// counts: returned to be recorded.
        /// </summary>
        private TrackedPBBlock? StartTracking(long entityId, MyProgrammableBlock block)
        {
            lock (_moves)
            {
                if (_resting.TryGetValue(entityId, out TrackedPBBlock? resting))
                {
                    // It has not run for a while, so this is the one full read of its names and state until it is active
                    // again; the cleanup pass only reads the cheap flags of resting blocks.
                    if (!resting.ProgrammableBlock.Closed && !resting.ProgrammableBlock.MarkedForClose)
                        resting.RefreshLiveState();
                    PBBlocks.TryAdd(entityId, resting);
                    _resting.TryRemove(entityId, out _);
                    return resting;
                }
            }

            // A block given to this player by someone else keeps its runs, offences and startup time, and this run counts:
            // otherwise handing it to another identity and back would give it a fresh startup allowance each time.
            TrackedPBBlock? handedOver = TrackingManager.TakeUp(this, block);
            if (handedOver is not null)
                return handedOver;

            PBBlocks.TryAdd(entityId, new TrackedPBBlock(block));
            return null;
        }

        /// <summary>
        /// Keeps a block given to this player by someone else as resting, with its history, until it runs here, unless this
        /// player already tracks it. Game thread, from TrackingManager.HandOver.
        /// </summary>
        internal void Rest(long entityId, TrackedPBBlock block)
        {
            lock (_moves)
            {
                if (!PBBlocks.ContainsKey(entityId))
                    _resting.TryAdd(entityId, block);
            }
        }

        /// <summary>Forgets a block handed to its new owner, if this player still lists that very entry. Game thread, from TrackingManager.HandOver.</summary>
        internal void Forget(long entityId, TrackedPBBlock block)
        {
            lock (_moves)
            {
                if (PBBlocks.TryGetValue(entityId, out TrackedPBBlock? tracked) && ReferenceEquals(tracked, block))
                    PBBlocks.TryRemove(entityId, out _);
                if (_resting.TryGetValue(entityId, out TrackedPBBlock? resting) && ReferenceEquals(resting, block))
                    _resting.TryRemove(entityId, out _);
            }
        }

        /// <summary>Lists a block taken up from its previous owner, see TrackingManager.TakeUp. Game thread.</summary>
        internal void List(long entityId, TrackedPBBlock block) => PBBlocks.TryAdd(entityId, block);

        /// <summary>
        /// Gives up a block that now belongs to someone else, tracked or resting, so its new owner's tracking takes it over
        /// with its history. False when this player does not have it, or still owns it. Game thread.
        /// </summary>
        public bool TryGiveUp(long entityId, out TrackedPBBlock? block)
        {
            lock (_moves)
            {
                if ((PBBlocks.TryGetValue(entityId, out block) || _resting.TryGetValue(entityId, out block))
                    && block.ProgrammableBlock.OwnerId != PlayerId)
                {
                    PBBlocks.TryRemove(entityId, out _);
                    _resting.TryRemove(entityId, out _);
                    return true;
                }
            }
            block = null;
            return false;
        }

        /// <summary>A copy of the tracked blocks, safe to keep and change. Blocks resting between rare runs are not in it.</summary>
        public List<TrackedPBBlock> GetPBBlocks() => new(PBBlocks.Values);

        /// <summary>
        /// As <see cref="GetPBBlocks"/>, with the blocks resting after they went idle too: a rarely run script rests between
        /// its runs, so turning a player's blocks off, the shutdown warning, the memory check and the reports include them.
        /// Each block once, also while the game thread moves one between the two.
        /// </summary>
        public List<TrackedPBBlock> GetAllPBBlocks() => CopyAll(static (block, _) => block);

        /// <summary>
        /// As <see cref="GetAllPBBlocks()"/>, each block with whether it was resting when the copy was taken. Reports add run
        /// times over the tracked ones only, as the combined limits do, and mark the resting ones.
        /// </summary>
        public List<(TrackedPBBlock Block, bool Resting)> GetAllPBBlocksWithState()
        {
            List<(TrackedPBBlock Block, bool Resting)> all = new(PBBlocks.Count + _resting.Count);
            CopyAllPBBlocksWithState(all);
            return all;
        }

        /// <summary>
        /// Fills <paramref name="target"/> with one copy of the tracked blocks, then the resting ones, under the lock the
        /// moves hold. The list is cleared first. Reports that gather repeatedly reuse their list instead of allocating
        /// one per player per refresh.
        /// </summary>
        internal void CopyAllPBBlocksWithState(List<(TrackedPBBlock Block, bool Resting)> target)
        {
            target.Clear();
            lock (_moves)
            {
                foreach (KeyValuePair<long, TrackedPBBlock> pair in PBBlocks)
                    target.Add((pair.Value, false));
                foreach (KeyValuePair<long, TrackedPBBlock> pair in _resting)
                    target.Add((pair.Value, true));
            }
        }

        /// <summary>One copy of the tracked blocks, then the resting ones, taken under the lock the moves hold.</summary>
        private List<T> CopyAll<T>(Func<TrackedPBBlock, bool, T> item)
        {
            lock (_moves)
            {
                List<T> all = new(PBBlocks.Count + _resting.Count);
                foreach (TrackedPBBlock block in PBBlocks.Values)
                    all.Add(item(block, false));
                foreach (TrackedPBBlock block in _resting.Values)
                    all.Add(item(block, true));
                return all;
            }
        }

        /// <summary>
        /// Whether a block's run times count in this player's combined run times, for the combined limits: not resting,
        /// whose last run may be long ago; running, or run since the last combined check and not turned off by the limiter
        /// or an admin after that run, as a dead or punished block keeps its last run time until it leaves tracking and
        /// uses nothing, while a script that turns its own block off after each run and is run again still uses it; and
        /// past its startup time, whose runs are ignored until it ends. Game thread only: it reads the live block state.
        /// Reports use <see cref="CountsInCombinedForReport"/>.
        /// </summary>
        public bool CountsInCombined(TrackedPBBlock block, bool resting) =>
            !resting && (block.IsRunning || block.RanSinceCombinedCheck) && !block.IsUnderGracePeriod(Privileged);

        /// <summary>
        /// As <see cref="CountsInCombined"/>, from any thread: it uses the working state the game thread cached in
        /// the block, for reports, which no longer gather on the game thread. The combined limits themselves keep
        /// using the live state, through <see cref="CountsInCombined"/>.
        /// </summary>
        public bool CountsInCombinedForReport(TrackedPBBlock block, bool resting) =>
            !resting && (block.IsRunningCached || block.RanSinceCombinedCheck) && !block.IsUnderGracePeriod(Privileged);

        /// <summary>The combined run times the combined limits check: the tracked blocks that count, see <see cref="CountsInCombined"/>.</summary>
        public CombinedRunTimes Combined()
        {
            CombinedRunTimes combined = new();
            foreach (KeyValuePair<long, TrackedPBBlock> kvp in PBBlocks)
                combined.Add(kvp.Value.LastRunTimeMS, kvp.Value.RunTimeMSAvg, CountsInCombined(kvp.Value, false));
            return combined;
        }

        /// <summary>
        /// Drops blocks that left the game, hands those that now belong to someone else over to be taken up by their new
        /// owner's tracking with their history, and rests blocks that have not run for RemoveInactivePBsAfterSeconds; those
        /// rest until they run again, are gone, change owner, or have not run for RemovePlayersWithNoPBFrequencyInMinutes.
        /// Game thread.
        /// </summary>
        public void CleanUpOldPBBlocks()
        {
            foreach (KeyValuePair<long, TrackedPBBlock> kvp in PBBlocks)
            {
                TrackedPBBlock tracked = kvp.Value;
                MyProgrammableBlock block = tracked.ProgrammableBlock;
                bool closed = block.Closed || block.MarkedForClose;
                // The names and working state reports show are cached here, so they stay fresh without the reports
                // reading the block themselves.
                if (!closed)
                    tracked.RefreshLiveState();
                if (!closed && block.OwnerId != PlayerId)
                {
                    TrackingManager.HandOver(this, kvp.Key, tracked);
                    continue;
                }
                if (!closed && tracked.LastUpdateTick.TicksTillNow_TimeSpan().TotalSeconds <= Config.RemoveInactivePBsAfterSeconds) continue;

                lock (_moves)
                {
                    // A recompile may have started it over meanwhile, off the game thread.
                    if (!closed && tracked.LastUpdateTick.TicksTillNow_TimeSpan().TotalSeconds <= Config.RemoveInactivePBsAfterSeconds) continue;
                    if (!closed)
                        _resting[kvp.Key] = tracked;
                    PBBlocks.TryRemove(kvp.Key, out _);
                }
            }

            int keepMinutes = Config.RemovePlayersWithNoPBFrequencyInMinutes;
            foreach (KeyValuePair<long, TrackedPBBlock> kvp in _resting)
            {
                MyProgrammableBlock block = kvp.Value.ProgrammableBlock;
                bool expired = keepMinutes > 0 && kvp.Value.LastUpdateTick.TicksTillNow_TimeSpan().TotalMinutes > keepMinutes;
                bool closed = block.Closed || block.MarkedForClose;
                // A resting block's names stay from its last run; only the cheap on/off flags are kept fresh, so this
                // pass does not read every idle block's names every few seconds. It refreshes fully when it runs again.
                if (!closed)
                    kvp.Value.RefreshEnabledState();
                if (!expired && !closed && block.OwnerId == PlayerId) continue;
                if (!expired && !closed)
                {
                    TrackingManager.HandOver(this, kvp.Key, kvp.Value);
                    continue;
                }
                lock (_moves)
                {
                    // A recompile may have tracked it again meanwhile, off the game thread: then the tracked loop's next
                    // pass decides.
                    if (!_resting.TryGetValue(kvp.Key, out TrackedPBBlock? still) || !ReferenceEquals(still, kvp.Value)) continue;
                    _resting.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Counts a recompile of the block, and with <paramref name="clearHistory"/> starts it over, with its startup time, keeping its count of recompiles; a block resting after it went idle is then
        /// tracked again first, so it starts over the same way. False when this player does not have the block. May run off
        /// the game thread.
        /// </summary>
        public bool Recompiled(MyProgrammableBlock pbBlock, bool clearHistory) => Recompiled(pbBlock, clearHistory, true);

        /// <summary>
        /// Starts the block over as <see cref="Recompiled(MyProgrammableBlock, bool)"/> does, for a recompile already counted
        /// while the block was still tracked under its previous owner. False when this player does not have the block.
        /// </summary>
        public bool StartOver(MyProgrammableBlock pbBlock) => Recompiled(pbBlock, true, false);

        private bool Recompiled(MyProgrammableBlock pbBlock, bool clearHistory, bool count)
        {
            TrackedPBBlock? trackedPBBlock;
            lock (_moves)
            {
                bool resting = true;
                if (!_resting.TryGetValue(pbBlock.EntityId, out trackedPBBlock))
                {
                    resting = false;
                    // Not finding it is normal: the "Check Code" button in the pb interface compiles in a block that isn't tracked.
                    if (!PBBlocks.TryGetValue(pbBlock.EntityId, out trackedPBBlock))
                        return false;
                }
                if (count)
                    trackedPBBlock.CountRecompile();
                if (!clearHistory)
                    return true;
                if (resting)
                {
                    PBBlocks[pbBlock.EntityId] = trackedPBBlock;
                    _resting.TryRemove(pbBlock.EntityId, out _);
                }

                // Under the lock too, so a cleanup pass can't take the block for idle again before its time starts over.
                trackedPBBlock.ClearRunTimes();
            }

            if (Config.DebugReporting)
                Log.Info($"Cleared the history of [{trackedPBBlock.BlockName}] after a recompile.");
            return true;
        }
    }
}
