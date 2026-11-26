using System.Collections.Concurrent;
using System.Text.Json;
using Advanced_PB_Limiter.QuasarContracts;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Companion;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>What the store did with a server's answer to a request the queue sent.</summary>
public enum AnswerOutcome
{
    /// <summary>The answer is recorded against the request.</summary>
    Recorded,
    /// <summary>
    /// The request was moved to a new incarnation of its cluster node while on its way, so the answer came from a node that
    /// is gone: it waits for the new node. See CompletePendingAsync for when the new node may already have it.
    /// </summary>
    Moved,
    /// <summary>The request's server was forgotten while it was on its way, so nothing is recorded. A newer request never replaces one on its way.</summary>
    Gone,
    /// <summary>Quasar stopped the store before the answer could be recorded: nothing is recorded, and the request is left as last saved.</summary>
    Stopping
}

/// <summary>Profiles, the queue and known servers, kept in one file under Quasar's install folder. All of it is safe to call from any thread.</summary>
public sealed class ProfileStore : IDisposable
{
    private const int MaxApplied = 25;

    // Limits a known server to being re-saved once in a while, every poll of every page would otherwise write the file.
    private const int SeenSaveMinutes = 5;

    // When each connected server was last seen, every time, between the saves SeenSaveMinutes limits. The page shows it,
    // and it is saved once the server is no longer connected, so a saved last-seen time is right to the pass that last
    // saw the server.
    private readonly ConcurrentDictionary<string, DateTime> _seenNow = new(StringComparer.Ordinal);

    // The highest queue number kept. Far below long.MaxValue, so counting on from any number at or under it cannot wrap
    // round to a negative one, which every applied profile would make look older and drop.
    private const long MaxSequence = long.MaxValue / 2;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<ProfileStore> _log;

    // Never changed once published, its entries included: a change edits a copy and replaces this, so readers need no lock.
    private volatile State _state = new();

    // One change at a time, each made on the newest state and saved before the next starts, so no change is lost to
    // another, the file ends with the newest state and a change is on disk when its task completes. The load holds it
    // until the file is read, so a change made earlier waits for that instead of being lost when the file's contents arrive.
    private readonly SemaphoreSlim _writer = new(0, 1);

    // Cancelled by Dispose, so a change waiting for the writer ends as stopping instead of waiting for good: disposing a
    // SemaphoreSlim does not wake its waiters. Only the wait for the writer takes it: the file reads and writes opt out,
    // so a load or a save already under way is never cut short.
    private readonly CancellationTokenSource _stopping = new();

    // Set only by a load that read the file or moved a broken one aside: an empty store must never replace the file.
    private volatile bool _canSave;

    /// <param name="path">The profiles file. Its folder is created on the first save.</param>
    /// <param name="log">Where load and save failures go.</param>
    public ProfileStore(string path, ILogger<ProfileStore> log)
    {
        _path = path;
        _log = log;
        SafeFile.Warning = message => log.LogWarning("{Message}", message);
        Loaded = LoadAsync();
    }

    /// <summary>Done once the saved file has been read, or found unreadable. It never faults.</summary>
    public Task Loaded { get; }

    /// <summary>False when the file could not be read at start: changes are then kept in memory only.</summary>
    public bool CanSave => !Loaded.IsCompleted || _canSave;

    /// <summary>Raised after any change, on whichever thread made it.</summary>
    public event Action? Changed;

    private int _disposed;
    private int _activeChanges;
    private int _fieldsDisposed;
    private int _cancelCompleted;

    public void Dispose()
    {
        // Once: Quasar may dispose it more than once as it stops.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Cancelling wakes a change waiting for the writer. The fields are disposed only once Cancel has returned and
        // the last change under way has left: disposing a source from its own cancellation notification, or disposing
        // the writer under a waiting change, is not safe.
        // Not yet disposed: only Dispose(true) disposes it, and only after _cancelCompleted is set below.
        _stopping.Cancel();
        Volatile.Write(ref _cancelCompleted, 1);
        if (Volatile.Read(ref _activeChanges) == 0)
            Dispose(true);
    }

    private void Dispose(bool disposing)
    {
        if (!disposing) return;
        if (Volatile.Read(ref _cancelCompleted) == 0) return;
        if (Interlocked.Exchange(ref _fieldsDisposed, 1) != 0) return;
        _writer.Dispose();
        _stopping.Dispose();
    }

    // Each returns a copy, sorted for display the same way on every host, that the caller may keep.
    public List<SavedProfile> GetProfiles() => _state.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // In the order they were queued, which the queue sends them in; their times may not be in order after a clock change.
    public List<PendingApply> GetPending() => _state.Pending.OrderBy(p => p.Sequence).ToList();

    public List<AppliedProfile> GetApplied() => _state.Applied.ToList();

    public List<KnownServer> GetKnownServers() => _state.KnownServers.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The known servers that <see cref="ServerInfo.PickKey"/>s name: by id, or by the cluster slot the node now in it
    /// holds. A queue request made after the page's last poll therefore still reaches a slot whose node restarted.
    /// </summary>
    public List<KnownServer> ResolveKnown(IEnumerable<string> keys)
    {
        List<KnownServer> known = GetKnownServers();
        List<KnownServer> resolved = new();
        foreach (string key in keys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            KnownServer? server;
            if (ServerPickKeys.TryCluster(key, out string clusterId, out string clusterSlot))
            {
                server = known.Find(s => string.Equals(s.ClusterId, clusterId, StringComparison.Ordinal)
                                         && string.Equals(s.ClusterSlot, clusterSlot, StringComparison.Ordinal));
            }
            else
            {
                string serverId = ServerPickKeys.ServerIdOf(key);
                server = serverId.Length == 0 ? null : known.Find(s => string.Equals(s.ServerId, serverId, StringComparison.Ordinal));
            }

            if (server is not null && !resolved.Contains(server))
                resolved.Add(server);
        }
        return resolved;
    }

    /// <summary>
    /// The server as it is known now: the same id, or for a cluster node that restarted, the node now in its slot. Null when
    /// it is no longer known. The same rule QueueAsync applies.
    /// </summary>
    public KnownServer? FindCurrent(KnownServer server) => Current(_state, server);

    /// <summary>The server listed with exactly this ServerId, with no cluster slot fallback. Null when none is.</summary>
    public KnownServer? FindById(string serverId) =>
        _state.KnownServers.Find(s => string.Equals(s.ServerId, serverId, StringComparison.Ordinal));


    public Task<StoreResult> SaveProfileAsync(SavedProfile profile)
    {
        return ChangeAsync(draft =>
        {
            State state = draft.Write;
            state.Profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            state.Profiles.Add(profile);
            return true;
        });
    }

    public Task<StoreResult> DeleteProfileAsync(string name)
    {
        // Names are unique ignoring case, as SaveProfileAsync replaces them.
        return ChangeAsync(draft => draft.Read.Profiles.Exists(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                                    && draft.Write.Profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0);
    }

    /// <summary>
    /// A server has one profile waiting at most: the newest request is the one the admin meant, so it replaces one still
    /// waiting, or one its server refused, which is logged. One already on its way is kept beside it until it is recorded,
    /// and the new one is sent after it. Each of
    /// <paramref name="servers"/> is looked up again when the change is made, as the caller's list may be old by then: a
    /// cluster node restarted since is queued under its new ServerId, and a server forgotten since is skipped.
    /// </summary>
    public async Task<QueueResult> QueueAsync(SavedProfile profile, IEnumerable<KnownServer> servers, string queuedBy)
    {
        DateTime now = DateTime.UtcNow;
        List<KnownServer> requested = servers.ToList();
        int queued = 0;
        bool renumbered = false;
        List<Dropped> dropped = new();
        StoreResult result = await ChangeAsync(draft =>
        {
            // Two chosen servers can now be one, an old incarnation of a node and its new one, which is queued once.
            List<KnownServer> targets = new(requested.Count);
            foreach (KnownServer server in requested)
            {
                KnownServer? current = Current(draft.Read, server);
                if (current != null && !targets.Contains(current))
                    targets.Add(current);
            }
            if (targets.Count == 0) return false;

            State state = draft.Write;
            // The load leaves the counter no further past MaxSequence than the requests it numbered, and one is added per
            // request, so this is not reached in practice; if it were, counting on would in time wrap round.
            if (state.LastSequence >= MaxSequence)
            {
                Renumber(draft);
                renumbered = true;
            }

            // One number for the request, for every server it goes to: one applied to an incarnation of a cluster slot
            // does not make the one for another incarnation older.
            long sequence = ++state.LastSequence;
            foreach (KnownServer server in targets)
            {
                // A request already on its way is left to finish and be recorded; once it has, the ordering rules drop or keep
                // what is left (SupersededBecause), and this newer one is sent after it.
                foreach (PendingApply replaced in state.Pending.Where(p => !p.Sending && string.Equals(p.ServerId, server.ServerId, StringComparison.Ordinal)).ToList())
                {
                    state.Pending.Remove(replaced);
                    // One still waiting is replaced by the admin's own choice; a refusal is told, as it was never cancelled.
                    if (replaced.Refused)
                        dropped.Add(new Dropped(replaced, "profile " + profile.Name + " was queued for it in its place"));
                }
                state.Pending.Add(new PendingApply
                {
                    ServerId = server.ServerId,
                    ServerName = server.DisplayName,
                    ProfileName = profile.Name,
                    SettingsJson = profile.SettingsJson,
                    QueuedBy = queuedBy,
                    QueuedUtc = now,
                    Sequence = sequence
                });
            }
            queued = targets.Count;
            return true;
        }).ConfigureAwait(false);
        if (renumbered && result.Changed)
            LogRenumbered("the counter reached its limit");
        LogDropped(dropped, result);
        return new QueueResult(result, queued);
    }

    /// <summary>
    /// The entry known now for <paramref name="server"/>, read from an earlier state: the one with its ServerId, or for a
    /// cluster node gone since, the one in its cluster slot, where <see cref="TakeOverSlot"/> or <see cref="Replaced"/>
    /// left its new incarnation. Null when it was forgotten.
    /// </summary>
    private static KnownServer? Current(State state, KnownServer server)
    {
        KnownServer? known = state.KnownServers.Find(s => string.Equals(s.ServerId, server.ServerId, StringComparison.Ordinal));
        if (known != null || string.IsNullOrEmpty(server.ClusterId) || string.IsNullOrEmpty(server.ClusterSlot))
            return known;
        return state.KnownServers.Find(s => string.Equals(s.ClusterId, server.ClusterId, StringComparison.Ordinal)
                                            && string.Equals(s.ClusterSlot, server.ClusterSlot, StringComparison.Ordinal));
    }

    /// <summary>Refused while the queue is sending it: a request on its way cannot be called back.</summary>
    public async Task<CancelResult> CancelPendingAsync(Guid id)
    {
        // Left as it is when Quasar stopped the store before the change could run: nothing was changed.
        CancelOutcome outcome = CancelOutcome.Stopping;
        StoreResult result = await ChangeAsync(draft =>
        {
            PendingApply? pending = draft.Read.Pending.Find(p => p.Id == id);
            if (pending == null)
            {
                outcome = CancelOutcome.NotWaiting;
                return false;
            }
            if (pending.Sending)
            {
                outcome = CancelOutcome.Sending;
                return false;
            }
            outcome = CancelOutcome.Cancelled;
            return draft.Write.Pending.Remove(pending);
        }).ConfigureAwait(false);
        return new CancelResult(outcome, result.Saved);
    }

    /// <summary>
    /// The server <paramref name="sent"/> went to refused it for good: it stays listed with the reason until cancelled, and
    /// is not sent again. <see cref="AnswerOutcome.Moved"/> when it was moved to a new incarnation of its cluster node
    /// while on its way: the refusal came from a node that is gone, so it waits for the new one instead.
    /// <see cref="AnswerOutcome.Gone"/> when it went with a forgotten server: there is nothing left to mark, and
    /// <see cref="AnswerOutcome.Stopping"/> when Quasar stopped the store first, the request left as it was. A newer
    /// request queued meanwhile is kept and sent either way.
    /// </summary>
    public async Task<AnswerOutcome> RefusePendingAsync(PendingApply sent, string reason)
    {
        // Left Stopping when Quasar stopped the store before the change could run: nothing was marked.
        AnswerOutcome outcome = AnswerOutcome.Stopping;
        await ChangeAsync(draft =>
        {
            PendingApply? pending = draft.Read.Pending.Find(p => p.Id == sent.Id);
            if (pending == null)
            {
                outcome = AnswerOutcome.Gone;
                return false;
            }
            PendingApply refused = draft.Edit(pending);
            refused.Sending = false;
            if (!string.Equals(pending.ServerId, sent.ServerId, StringComparison.Ordinal))
            {
                outcome = AnswerOutcome.Moved;
                return true;
            }
            outcome = AnswerOutcome.Recorded;
            refused.Refused = true;
            refused.LastError = reason;
            return true;
        }).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Claims a request for sending to <paramref name="serverId"/>, so a cancel from now on is refused, and returns it as
    /// claimed: what is sent and recorded is the request as it is now, not the copy the queue read earlier. Null when it
    /// is gone, cancelled or replaced since the queue read it, or moved to a new incarnation of its cluster node, which
    /// the queue has not checked is connected. Null too, and the request dropped, when it is older than a profile already
    /// applied to its cluster slot by any incarnation still listed, or than one waiting for the same server: it is never
    /// applied after a newer one. The drop is saved and logged. The mark is not written on its own; a load clears it.
    /// </summary>
    public async Task<PendingApply?> TryStartSendingAsync(Guid id, string serverId)
    {
        // Left null when Quasar stopped the store before the change could run.
        PendingApply? claimed = null;
        bool superseded = false;
        await ChangeAsync(draft =>
        {
            PendingApply? found = draft.Read.Pending.Find(p => p.Id == id && string.Equals(p.ServerId, serverId, StringComparison.Ordinal));
            if (found == null || found.Sending) return false;
            if (SupersededBecause(draft.Read, found) != null)
            {
                superseded = true;
                return false;
            }
            claimed = draft.Edit(found);
            claimed.Sending = true;
            return true;
        }, persist: false).ConfigureAwait(false);

        // Dropped in a change of its own: the claim is kept in memory only, and a dropped request has to stay dropped
        // after a restart.
        if (superseded)
            await DropSupersededAsync(id).ConfigureAwait(false);
        return claimed;
    }

    /// <summary>Drops a request a newer one has superseded, if it still is and is not being sent.</summary>
    private async Task DropSupersededAsync(Guid id)
    {
        List<Dropped> dropped = new(1);
        StoreResult result = await ChangeAsync(draft =>
        {
            PendingApply? pending = draft.Read.Pending.Find(p => p.Id == id);
            if (pending is not { Sending: false }) return false;
            string? why = SupersededBecause(draft.Read, pending);
            if (why == null || !draft.Write.Pending.Remove(pending)) return false;
            dropped.Add(new Dropped(pending, why));
            return true;
        }).ConfigureAwait(false);
        LogDropped(dropped, result);
    }

    /// <summary>
    /// After a send that got no answer, in time or before the agent dropped: the request waits again with the reason, can be
    /// cancelled, and is marked as possibly applied, which its drop, if a newer one supersedes it, says.
    /// </summary>
    public Task<StoreResult> TimedOutAsync(Guid id, string error)
    {
        // Saved only when the mark or the reason is new: a server that keeps timing out would otherwise rewrite the same
        // file on every pass.
        bool news = _state.Pending.Find(p => p.Id == id) is not { MayBeApplied: true } found
                    || !string.Equals(found.LastError, error, StringComparison.Ordinal);
        return ChangeAsync(draft =>
        {
            PendingApply? pending = draft.Read.Pending.Find(p => p.Id == id);
            if (pending == null) return false;
            PendingApply edited = draft.Edit(pending);
            edited.Sending = false;
            edited.MayBeApplied = true;
            edited.LastError = error;
            return true;
        }, persist: news);
    }

    /// <summary>After a failed send: the request waits again, and can be cancelled.</summary>
    public Task<StoreResult> StopSendingAsync(Guid id)
    {
        return ChangeAsync(draft =>
        {
            PendingApply? pending = draft.Read.Pending.Find(p => p.Id == id && p.Sending);
            if (pending == null) return false;
            draft.Edit(pending).Sending = false;
            return true;
        }, persist: false);
    }

    /// <summary>
    /// Records <paramref name="sent"/> as applied to the server it went to. <see cref="AnswerOutcome.Moved"/> when it was
    /// moved to a new incarnation of its cluster node while on its way: the node it reached is gone, so it waits for the new
    /// node. On a managed cluster that applies it once: the 1.3.3 limiter refuses profiles on a managed node, and PluginSdk
    /// refuses any save that differs from the cluster's configuration, so only a limiter before 1.3.3 took it, live and
    /// unsaved, and it went with the node. On a cluster Quasar
    /// does not manage, whose nodes may read one config file, the old node may have saved it there, and the new node then
    /// gets it a second time. <see cref="AnswerOutcome.Gone"/> when it went with a forgotten server, and
    /// <see cref="AnswerOutcome.Stopping"/> when Quasar stopped the store first: nothing is recorded.
    /// A newer request queued meanwhile is applied after it and is what the history and the server's last applied time show.
    /// When recorded, a request queued earlier for another incarnation of the same cluster slot is dropped, and logged, as
    /// it would now be applied after a newer one; one on its way is left to finish, and the claim drops it if it fails.
    /// The request's sequence is read from the store, not from <paramref name="sent"/>: a reset of the queue numbers
    /// while it was on its way renumbers the stored one only.
    /// </summary>
    public async Task<AnswerOutcome> CompletePendingAsync(PendingApply sent, int settings, int privilegedPlayers)
    {
        // Left Stopping when Quasar stopped the store before the change could run: nothing was recorded.
        AnswerOutcome outcome = AnswerOutcome.Stopping;
        List<Dropped> dropped = new();
        StoreResult result = await ChangeAsync(draft =>
        {
            // By id: a newer request for this server may wait beside it, queued while this one was on its way, and still has to run.
            PendingApply? pending = draft.Read.Pending.Find(p => p.Id == sent.Id);
            if (pending == null)
            {
                outcome = AnswerOutcome.Gone;
                return false;
            }
            if (!string.Equals(pending.ServerId, sent.ServerId, StringComparison.Ordinal))
            {
                outcome = AnswerOutcome.Moved;
                PendingApply moved = draft.Edit(pending);
                moved.Sending = false;
                moved.MayBeApplied = true; // By the node that was replaced.
                return true;
            }

            outcome = AnswerOutcome.Recorded;
            State state = draft.Write;
            state.Pending.Remove(pending);
            KnownServer? server = state.KnownServers.Find(s => string.Equals(s.ServerId, sent.ServerId, StringComparison.Ordinal));
            if (server != null)
            {
                long sequence = pending.Sequence;
                if (server.LastAppliedSequence < sequence)
                    draft.Edit(server).LastAppliedSequence = sequence;

                // What still waits for another incarnation of this slot and was queued before this one would otherwise be
                // applied after it, when that incarnation connects or is replaced. One queued with it, for both, shares its
                // sequence: the same request, which the admin made once for the slot, one server to them, and which one
                // incarnation now has, so its copy is dropped too rather than applied a second time. One already on its way cannot be called back, so it is left to finish rather
                // than logged as dropped unsent.
                foreach (PendingApply older in state.Pending.Where(p => p.Sequence <= sequence && !p.Sending && InSlot(state, p.ServerId, server)).ToList())
                {
                    state.Pending.Remove(older);
                    dropped.Add(new Dropped(older, older.Sequence == sequence
                        ? "the same request was applied to " + sent.ServerName + " in the same cluster slot"
                        : "profile " + sent.ProfileName + ", queued after it, was applied to " + sent.ServerName + " in the same cluster slot"));
                }
            }
            state.Applied.Insert(0, new AppliedProfile
            {
                ServerName = sent.ServerName,
                ProfileName = sent.ProfileName,
                QueuedBy = sent.QueuedBy,
                AppliedUtc = DateTime.UtcNow,
                Settings = settings,
                PrivilegedPlayers = privilegedPlayers
            });
            if (state.Applied.Count > MaxApplied)
                state.Applied.RemoveRange(MaxApplied, state.Applied.Count - MaxApplied);
            return true;
        }).ConfigureAwait(false);
        LogDropped(dropped, result);
        return outcome;
    }

    /// <summary>Records why a profile is still waiting, for the Profiles page. Changed when that is a new reason.</summary>
    public Task<StoreResult> FailPendingAsync(Guid id, string error)
    {
        return ChangeAsync(draft =>
        {
            PendingApply? pending = draft.Read.Pending.Find(p => p.Id == id);
            if (pending == null || string.Equals(pending.LastError, error, StringComparison.Ordinal)) return false;
            draft.Edit(pending).LastError = error;
            return true;
        });
    }

    /// <summary>
    /// <see cref="ServersSeenAsync"/> for a page refreshing its list: never throws, a failure is logged, and the page
    /// shows the list it already has.
    /// </summary>
    public async Task RecordSeenAsync(IEnumerable<QuasarCompanionServer> servers)
    {
        try
        {
            await ServersSeenAsync(servers).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not record the connected PB Limiter servers");
        }
    }

    /// <summary>
    /// Saves the exact last-seen times of the servers connected now, as Quasar stops, so they are right after a restart:
    /// between stops they are saved only every few minutes. Never throws; a failure is logged.
    /// </summary>
    public async Task SaveSeenAsync()
    {
        List<KeyValuePair<string, DateTime>> seen = _seenNow.ToList();
        if (seen.Count == 0) return;
        try
        {
            await ChangeAsync(draft => KeepLastSeen(draft, seen)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not save when the PB Limiter servers were last seen");
        }
    }

    /// <summary>Sets each known server's last-seen time to the one given, where that is later. True when one changed.</summary>
    private static bool KeepLastSeen(Draft draft, List<KeyValuePair<string, DateTime>> times)
    {
        bool changed = false;
        foreach (KeyValuePair<string, DateTime> last in times)
        {
            KnownServer? known = draft.Read.KnownServers.Find(s => string.Equals(s.ServerId, last.Key, StringComparison.Ordinal));
            if (known == null || known.LastSeenUtc >= last.Value) continue;
            draft.Edit(known).LastSeenUtc = last.Value;
            changed = true;
        }
        return changed;
    }

    /// <summary>When the server was last seen connected: also between the saves of its last-seen time.</summary>
    public DateTime LastSeenUtc(KnownServer server) =>
        _seenNow.TryGetValue(server.ServerId, out DateTime seen) && seen > server.LastSeenUtc ? seen : server.LastSeenUtc;

    public Task<StoreResult> ServersSeenAsync(IEnumerable<QuasarCompanionServer> servers)
    {
        DateTime now = DateTime.UtcNow;
        // Quasar cluster nodes run no limiter, so they never take a profile: they are not recorded.
        List<QuasarCompanionServer> seen = servers.Where(server => string.IsNullOrEmpty(server.ClusterId)).ToList();
        foreach (QuasarCompanionServer server in seen)
            _seenNow[server.ServerId] = now;
        List<KeyValuePair<string, DateTime>> gone = _seenNow.Where(kvp => !seen.Exists(s => string.Equals(s.ServerId, kvp.Key, StringComparison.Ordinal))).ToList();
        // Asked on every page refresh and queue pass: the store is copied only when there is something to change.
        State current = _state;
        if (gone.Count == 0 && !seen.Exists(server => IsNews(current, server, now)) && Replaced(current, seen).Count == 0
            && !current.KnownServers.Exists(IsClusterNode))
            return Task.FromResult(new StoreResult(false, true));

        return RecordServersAsync(seen, now, gone);
    }

    /// <summary>The change <see cref="ServersSeenAsync"/> makes when there is one. Requests it drops while moving them are logged.</summary>
    private async Task<StoreResult> RecordServersAsync(List<QuasarCompanionServer> seen, DateTime now, List<KeyValuePair<string, DateTime>> gone)
    {
        List<Dropped> dropped = new();
        int clusterNodes = 0;
        StoreResult result = await ChangeAsync(draft =>
        {
            // No longer connected: the last time it was seen is kept.
            bool changed = KeepLastSeen(draft, gone);

            // A cluster node an earlier version recorded goes, with anything waiting for it, which it would never apply.
            HashSet<string> nodes = draft.Read.KnownServers.Where(IsClusterNode).Select(node => node.ServerId).ToHashSet(StringComparer.Ordinal);
            if (nodes.Count > 0)
            {
                draft.Write.KnownServers.RemoveAll(known => nodes.Contains(known.ServerId));
                draft.Write.Pending.RemoveAll(pending => nodes.Contains(pending.ServerId));
                clusterNodes = nodes.Count;
                changed = true;
            }

            foreach (QuasarCompanionServer server in seen)
            {
                KnownServer? known = draft.Read.KnownServers.Find(s => string.Equals(s.ServerId, server.ServerId, StringComparison.Ordinal));
                if (known == null && TakeOverSlot(draft, server, seen, now, dropped))
                {
                    changed = true;
                }
                else if (known == null)
                {
                    draft.Write.KnownServers.Add(new KnownServer
                    {
                        ServerId = server.ServerId, DisplayName = server.DisplayName, LastSeenUtc = now,
                        ClusterId = server.ClusterId, ClusterSlot = server.ClusterSlot,
                    });
                    changed = true;
                }
                else if (IsNews(draft.Read, server, now))
                {
                    KnownServer renewed = draft.Edit(known);
                    renewed.DisplayName = server.DisplayName;
                    renewed.ClusterId = server.ClusterId;
                    renewed.ClusterSlot = server.ClusterSlot;
                    renewed.LastSeenUtc = now;
                    changed = true;
                }
            }

            // A node that connected while its old incarnation was still listed was added on its own; once the old one
            // is gone, what waited for it moves to the node now in its slot, and so does what was applied to it, which an
            // older request for the slot is compared against.
            foreach ((KnownServer old, QuasarCompanionServer node) in Replaced(draft.Read, seen))
            {
                MovePending(draft, old.ServerId, node, dropped);
                KnownServer? survivor = draft.Read.KnownServers.Find(s => string.Equals(s.ServerId, node.ServerId, StringComparison.Ordinal));
                if (survivor != null && survivor.LastAppliedSequence < old.LastAppliedSequence)
                    draft.Edit(survivor).LastAppliedSequence = old.LastAppliedSequence;
                draft.Write.KnownServers.Remove(old);
                changed = true;
            }
            return changed;
        }).ConfigureAwait(false);
        // Only its own entry: one that connected again meanwhile has a newer time and stays.
        foreach (KeyValuePair<string, DateTime> offline in gone)
            ((ICollection<KeyValuePair<string, DateTime>>)_seenNow).Remove(offline);
        LogDropped(dropped, result);
        if (clusterNodes > 0 && result.Changed)
            _log.LogInformation("Dropped {Count} Quasar cluster node(s) from the PB Limiter servers, with any profile waiting for them: the limiter does not run on cluster nodes", clusterNodes);
        return result;
    }

    private static bool IsClusterNode(KnownServer server) => !string.IsNullOrEmpty(server.ClusterId);

    /// <summary>Known cluster nodes no longer connected whose slot a connected node with another ServerId now holds.</summary>
    private static List<(KnownServer Old, QuasarCompanionServer Node)> Replaced(State state, List<QuasarCompanionServer> connected)
    {
        List<(KnownServer, QuasarCompanionServer)> replaced = new();
        foreach (KnownServer known in state.KnownServers)
        {
            if (string.IsNullOrEmpty(known.ClusterId) || string.IsNullOrEmpty(known.ClusterSlot)) continue;
            if (connected.Exists(c => string.Equals(c.ServerId, known.ServerId, StringComparison.Ordinal))) continue;
            QuasarCompanionServer? node = connected.Find(c => string.Equals(c.ClusterId, known.ClusterId, StringComparison.Ordinal)
                                                              && string.Equals(c.ClusterSlot, known.ClusterSlot, StringComparison.Ordinal));
            if (node != null)
                replaced.Add((known, node));
        }
        return replaced;
    }

    /// <summary>
    /// What waits for one server now waits for another. A server has one request at a time, the newest queued: a moved
    /// request older than one already waiting for the new server, or not newer than one applied to it or to any
    /// incarnation of its cluster slot still listed, is dropped, and so is an older one waiting there for a newer one
    /// moved in. Each drop goes into <paramref name="dropped"/>. A request on its way is never dropped: it cannot be called
    /// back, and may still be applied. One that would be is moved all the same, so it is not left waiting for a server that
    /// is gone, and an older one on its way to the new server stays beside the one moved in. Once such a request is
    /// answered or fails, the claim drops whichever of the two is older before it is sent again.
    /// </summary>
    private static void MovePending(Draft draft, string fromServerId, QuasarCompanionServer to, List<Dropped> dropped)
    {
        foreach (PendingApply pending in draft.Read.Pending.Where(p => string.Equals(p.ServerId, fromServerId, StringComparison.Ordinal)).ToList())
        {
            State state = draft.Write;
            bool newerWaiting = state.Pending.Exists(p => p != pending && string.Equals(p.ServerId, to.ServerId, StringComparison.Ordinal) && p.Sequence >= pending.Sequence);
            // Equal is the same request, queued for both incarnations and already applied to the other.
            bool newerApplied = SlotApplied(state, to.ServerId, to.ClusterId, to.ClusterSlot) >= pending.Sequence;
            if ((newerWaiting || newerApplied) && !pending.Sending)
            {
                state.Pending.Remove(pending);
                dropped.Add(new Dropped(pending, newerWaiting
                    ? "a profile queued after it already waits for " + to.DisplayName + ", the node now in its cluster slot"
                    : "it, or a profile queued after it, was already applied to its cluster slot"));
                continue;
            }

            // Only when the moved request is the newest for the new server, so what waits there is older.
            if (!newerWaiting && !newerApplied)
            {
                foreach (PendingApply older in state.Pending.Where(p => p != pending && !p.Sending && string.Equals(p.ServerId, to.ServerId, StringComparison.Ordinal)).ToList())
                {
                    state.Pending.Remove(older);
                    dropped.Add(new Dropped(older, "profile " + pending.ProfileName + ", queued after it for the same cluster slot, now waits for " + to.DisplayName + " instead"));
                }
            }
            PendingApply moved = draft.Edit(pending);
            moved.ServerId = to.ServerId;
            moved.ServerName = to.DisplayName;
        }
    }

    /// <summary>True when the server is new, renamed, moved in its cluster, or due for its last-seen time to be saved again.</summary>
    private static bool IsNews(State state, QuasarCompanionServer server, DateTime now)
    {
        KnownServer? known = state.KnownServers.Find(s => string.Equals(s.ServerId, server.ServerId, StringComparison.Ordinal));
        return known == null
               || !string.Equals(known.DisplayName, server.DisplayName, StringComparison.Ordinal)
               || !string.Equals(known.ClusterId, server.ClusterId, StringComparison.Ordinal)
               || !string.Equals(known.ClusterSlot, server.ClusterSlot, StringComparison.Ordinal)
               || now - known.LastSeenUtc > TimeSpan.FromMinutes(SeenSaveMinutes);
    }

    /// <summary>
    /// A new incarnation of a known cluster node takes over its slot: what waits for the old ServerId is meant for this
    /// node, and the old one never connects again. False for a server that is not a cluster node, a slot not seen yet,
    /// or a slot whose node is still connected, while one replaces the other.
    /// </summary>
    private static bool TakeOverSlot(Draft draft, QuasarCompanionServer server, List<QuasarCompanionServer> connected, DateTime now, List<Dropped> dropped)
    {
        if (string.IsNullOrEmpty(server.ClusterId) || string.IsNullOrEmpty(server.ClusterSlot)) return false;
        KnownServer? slot = draft.Read.KnownServers.Find(s => string.Equals(s.ClusterId, server.ClusterId, StringComparison.Ordinal)
                                                         && string.Equals(s.ClusterSlot, server.ClusterSlot, StringComparison.Ordinal));
        if (slot == null || connected.Exists(c => string.Equals(c.ServerId, slot.ServerId, StringComparison.Ordinal))) return false;

        MovePending(draft, slot.ServerId, server, dropped);
        // Another incarnation of the slot may still be listed, until Replaced drops it: the newest applied to any of them
        // is what an older request for the slot is compared against from now on.
        long applied = SlotApplied(draft.Read, server.ServerId, server.ClusterId, server.ClusterSlot);
        KnownServer taken = draft.Edit(slot);
        taken.ServerId = server.ServerId;
        taken.DisplayName = server.DisplayName;
        taken.LastSeenUtc = now;
        if (taken.LastAppliedSequence < applied)
            taken.LastAppliedSequence = applied;
        return true;
    }

    /// <summary>
    /// The sequence of the newest profile applied to <paramref name="serverId"/>, or to any entry listed in the same
    /// cluster slot: one slot is one server to the admin, whichever incarnation a profile reached. 0 when none is known.
    /// </summary>
    private static long SlotApplied(State state, string serverId, string clusterId, string clusterSlot)
    {
        bool clustered = !string.IsNullOrEmpty(clusterId) && !string.IsNullOrEmpty(clusterSlot);
        long newest = 0;
        foreach (KnownServer known in state.KnownServers)
        {
            bool same = string.Equals(known.ServerId, serverId, StringComparison.Ordinal)
                        || (clustered && string.Equals(known.ClusterId, clusterId, StringComparison.Ordinal)
                                      && string.Equals(known.ClusterSlot, clusterSlot, StringComparison.Ordinal));
            if (same && known.LastAppliedSequence > newest)
                newest = known.LastAppliedSequence;
        }
        return newest;
    }

    /// <summary>True when <paramref name="serverId"/> is <paramref name="server"/>, or a listed entry in its cluster slot.</summary>
    private static bool InSlot(State state, string serverId, KnownServer server)
    {
        if (string.Equals(serverId, server.ServerId, StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(server.ClusterId) || string.IsNullOrEmpty(server.ClusterSlot)) return false;
        return state.KnownServers.Exists(s => string.Equals(s.ServerId, serverId, StringComparison.Ordinal)
                                              && string.Equals(s.ClusterId, server.ClusterId, StringComparison.Ordinal)
                                              && string.Equals(s.ClusterSlot, server.ClusterSlot, StringComparison.Ordinal));
    }

    /// <summary>
    /// Why <paramref name="pending"/> must not be sent, or null when it may be: sending it would apply it after a newer
    /// profile. Either one queued after it was already applied to its server's cluster slot, or one waits for the same
    /// server, which <see cref="MovePending"/> leaves when it moves a request on its way. Decided by sequence, so a clock
    /// set back cannot make a new request look older. One queued with it for another incarnation shares its sequence: once
    /// either copy is applied to the slot the other is not sent, as <see cref="MovePending"/> decides too. Two copies of one
    /// request moved onto one server are the same profile, and the one with the higher Id is the copy dropped.
    /// </summary>
    private static string? SupersededBecause(State state, PendingApply pending)
    {
        if (state.Pending.Exists(p => p != pending && string.Equals(p.ServerId, pending.ServerId, StringComparison.Ordinal)
                                      && (p.Sequence > pending.Sequence || (p.Sequence == pending.Sequence && p.Id.CompareTo(pending.Id) < 0))))
            return "the same profile, or one queued after it, already waits for this server";
        KnownServer? server = state.KnownServers.Find(s => string.Equals(s.ServerId, pending.ServerId, StringComparison.Ordinal));
        return server != null && pending.Sequence <= SlotApplied(state, server.ServerId, server.ClusterId, server.ClusterSlot)
            ? "it, or a profile queued after it, was already applied to this server or its cluster slot"
            : null;
    }

    /// <summary>
    /// A request taken off the queue because a newer one was applied or waits in its place, and why: either never sent, or
    /// sent and refused by its server, listed until then for the admin to cancel.
    /// </summary>
    private readonly record struct Dropped(PendingApply Request, string Why);

    /// <summary>
    /// Logs the requests a change dropped. The admin did not cancel them, so each is told, as refused or possibly applied
    /// when the store knows that. Nothing is logged when Quasar stopped the store before the change was made: nothing was
    /// dropped then.
    /// </summary>
    private void LogDropped(List<Dropped> dropped, StoreResult result)
    {
        if (!result.Changed || dropped.Count == 0 || !_log.IsEnabled(LogLevel.Information)) return;
        foreach (Dropped drop in dropped)
        {
            // A profile saved before names were checked can hold line breaks or hidden characters: a log line must not
            // be forgeable through one.
            string profile = drop.Request.ProfileName.SafeName();
            string server = drop.Request.ServerName.SafeName();
            string why = drop.Why.SafeName();
            if (drop.Request.Refused)
                _log.LogInformation("PB Limiter profile {Profile} for {Server}, which the server refused, was taken off the queue, as {Reason}",
                    profile, server, why);
            else if (drop.Request.MayBeApplied)
                _log.LogInformation("PB Limiter profile {Profile} for {Server}, which may have been applied, was taken off the queue, as {Reason}",
                    profile, server, why);
            else
                _log.LogInformation("PB Limiter profile {Profile} for {Server} was taken off the queue, as {Reason}",
                    profile, server, why);
        }
    }

    /// <summary>Drops a server and anything waiting for it.</summary>
    public Task<StoreResult> ForgetServerAsync(string serverId)
    {
        return ChangeAsync(draft =>
        {
            if (!draft.Read.KnownServers.Exists(s => string.Equals(s.ServerId, serverId, StringComparison.Ordinal))
                && !draft.Read.Pending.Exists(p => string.Equals(p.ServerId, serverId, StringComparison.Ordinal)))
                return false;
            State state = draft.Write;
            int servers = state.KnownServers.RemoveAll(s => string.Equals(s.ServerId, serverId, StringComparison.Ordinal));
            int pending = state.Pending.RemoveAll(p => string.Equals(p.ServerId, serverId, StringComparison.Ordinal));
            return servers + pending > 0;
        });
    }

    /// <summary>
    /// Applies one change and saves it. <paramref name="change"/> reads the state through its draft, writes through it only
    /// when it has something to change, and returns whether it changed anything. It runs once, under the writer, and not
    /// at all when Quasar stopped the store first. <paramref name="persist"/> false keeps a change that needs no saving
    /// (the in-flight mark) in memory only.
    /// </summary>
    private async Task<StoreResult> ChangeAsync(Func<Draft, bool> change, bool persist = true)
    {
        if (Volatile.Read(ref _disposed) != 0) return new StoreResult(false, true);
        Interlocked.Increment(ref _activeChanges);
        try
        {
            bool published = false;
            bool written = false;
            try
            {
                bool saved = true;
                await _writer.WaitAsync(_stopping.Token).ConfigureAwait(false);
                try
                {
                    // Made under the writer, so it works on the newest state and no other change is published meanwhile.
                    // A change that wrote nothing changed nothing, whatever it returned.
                    Draft draft = new(_state);
                    State? next = change(draft) ? draft.Result : null;
                    if (next != null)
                    {
                        _state = next;
                        published = true;
                        if (persist)
                            saved = _canSave && await WriteAsync(JsonSerializer.SerializeToUtf8Bytes(next, Json)).ConfigureAwait(false);
                        written = saved;
                    }
                }
                finally
                {
                    _writer.Release();
                }

                if (published)
                    RaiseChanged();
                return new StoreResult(published, saved);
            }
            catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException)
            {
                // Stopped part way, or before it could start: a change already made, and saved, stays made, and is reported so.
                _log.LogWarning(e, "A PB Limiter profile change came while Quasar was stopping the store, it may not be saved");
                return new StoreResult(published, written);
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeChanges) == 0 && Volatile.Read(ref _disposed) != 0)
                Dispose(true);
        }
    }

    private async Task LoadAsync()
    {
        State loaded = new();
        bool canSave = false;
        try
        {
            await RecoverInterruptedSaveAsync().ConfigureAwait(false);
            if (File.Exists(_path))
                loaded = Normalise(Parse(await File.ReadAllBytesAsync(_path, CancellationToken.None).ConfigureAwait(false))); // Never cut short, see _stopping.
            canSave = true;
        }
        catch (JsonException e)
        {
            canSave = SetAside(e);
        }
        catch (Exception e)
        {
            // Any failure, expected or not: Loaded must not fault, the profile queue waits on it.
            _log.LogError(e, "Could not read the PB Limiter profiles, nothing is saved until Quasar restarts");
        }
        finally
        {
            _state = loaded;
            _canSave = canSave;
            ReleaseWriter();
        }
        RaiseChanged();
    }

    /// <summary>
    /// Puts back a complete copy an interrupted save left beside the profiles (<see cref="SafeFile.RecoverAsync"/>), before
    /// the file is read, so a crash between writing the copy and moving it into place cannot lose the last change.
    /// </summary>
    private async Task RecoverInterruptedSaveAsync()
    {
        string temporary = _path + ".tmp";
        try
        {
            SafeFile.RecoverResult recovery = await SafeFile.RecoverAsync(_path, temporary, IsWholeJson).ConfigureAwait(false);
            string? line = SafeFile.Describe(recovery.Recovery, _path, temporary, recovery.Kept);
            if (line is not null)
                _log.LogWarning("{Recovery}", line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The copy is kept for the next start; the profiles are read as they are.
            _log.LogWarning(e, "Could not restore the PB Limiter profiles from {File}; they are read as they are", temporary);
        }
    }

    /// <summary>
    /// True when the bytes are one whole JSON document. A save cut short fails this, so <see cref="SafeFile"/> will not
    /// put it back; one whole document is a copy the admin's newest profiles may be in.
    /// </summary>
    private static bool IsWholeJson(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(Utf8Bom))
            json = json[3..];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps a file that is not valid JSON for the admin to look at; the next save would otherwise replace it. An earlier
    /// file set aside is kept too.
    /// </summary>
    private bool SetAside(JsonException error)
    {
        try
        {
            // A name of its own: .unreadable, or with the time, and a number, when an earlier file holds that name.
            string aside = KeptFile.Keep(_path, ".unreadable");
            _log.LogError(error, "Could not read the PB Limiter profiles, moved them to {File}", aside);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogError(e, "Could not read the PB Limiter profiles or move them aside, nothing is saved until Quasar restarts");
            return false;
        }
    }

    // Through SafeFile: a complete copy swapped in with one rename, written in place only where the swap fails, and one
    // save of the file at a time, also with another Quasar worker during a restart. A complete copy an earlier write left
    // beside a missing or cut-short file is put back by RecoverInterruptedSaveAsync.
    private async Task<bool> WriteAsync(byte[] json)
    {
        string temporary = _path + ".tmp";
        try
        {
            string? folder = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            SafeFile.WriteResult write = await SafeFile.WriteAsync(_path, temporary, json, IsWholeJson).ConfigureAwait(false);
            if (write.Kept is not null)
                _log.LogWarning("A complete copy of the PB Limiter profiles an earlier save left at {File} was kept as {Kept} before this save, in case it holds profiles you want.", temporary, write.Kept);
            if (write.SwapError is not null)
                // For example a file mounted on its own into a container, or held open by another program.
                _log.LogWarning(write.SwapError, "The PB Limiter profiles could not be swapped into place, so they are written in place");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogError(e, "Could not save the PB Limiter profiles");
            return false;
        }
    }

    private void ReleaseWriter()
    {
        try
        {
            _writer.Release();
        }
        catch (ObjectDisposedException e)
        {
            _log.LogDebug(e, "The PB Limiter profile store was stopped during a change");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            // The change is already made; a listener's failure must not fail it, or fault Loaded.
            _log.LogError(e, "A PB Limiter profile listener failed");
        }
    }

    /// <summary>
    /// The saved file's state. A UTF-8 byte order mark is skipped: an editor or Windows PowerShell 5.1's
    /// Set-Content -Encoding UTF8 writes one, and the serializer would refuse the whole file over it.
    /// </summary>
    private static ReadOnlySpan<byte> Utf8Bom => new byte[] { 0xEF, 0xBB, 0xBF };

    private static State? Parse(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(Utf8Bom))
            json = json[3..];
        return JsonSerializer.Deserialize<State>(json, Json);
    }

    /// <summary>
    /// A hand-edited file may hold null lists or entries, which every reader would trip over, or queue numbers that are
    /// negative or so large that counting on from them would wrap round. Those numbers are reset, and the reset logged.
    /// </summary>
    private State Normalise(State? state)
    {
        if (state == null) return new State();
        state.Profiles = WithoutNulls(state.Profiles);
        state.Pending = WithoutNulls(state.Pending);
        // A mark left by a Quasar that stopped mid-send: nothing is sending now, so the request waits again.
        foreach (PendingApply pending in state.Pending)
            pending.Sending = false;
        state.Applied = WithoutNulls(state.Applied);
        state.KnownServers = WithoutNulls(state.KnownServers);
        if (!SequencesInRange(state))
        {
            Draft draft = new(state);
            Renumber(draft);
            state = draft.Write;
            LogRenumbered("the profiles file held some that were negative or too large to count on from");
        }
        NumberUnsequenced(state);
        return state;
    }

    private static bool SequencesInRange(State state) =>
        state.LastSequence is >= 0 and <= MaxSequence
        && state.Pending.TrueForAll(p => p.Sequence is >= 0 and <= MaxSequence)
        && state.KnownServers.TrueForAll(s => s.LastAppliedSequence is >= 0 and <= MaxSequence);

    /// <summary>
    /// Numbers the waiting requests again from 1, in the order they were queued, those queued together still sharing a
    /// number; a request from an earlier version, numbered 0, is left for <see cref="NumberUnsequenced"/>. What was last
    /// applied to each server is forgotten, set to 0: its old number cannot be compared with the new ones, and nothing
    /// known drops nothing, where a wrong number could drop every new request.
    /// </summary>
    private static void Renumber(Draft draft)
    {
        State state = draft.Write;
        long last = 0;
        foreach (IGrouping<long, PendingApply> together in state.Pending.Where(p => p.Sequence != 0).GroupBy(p => p.Sequence).OrderBy(g => g.Key).ToList())
        {
            last++;
            foreach (PendingApply pending in together)
                draft.Edit(pending).Sequence = last;
        }
        foreach (KnownServer known in state.KnownServers.Where(s => s.LastAppliedSequence != 0).ToList())
            draft.Edit(known).LastAppliedSequence = 0;
        state.LastSequence = last;
    }

    /// <summary>Tells the admin the queue numbers were reset, and <paramref name="why"/>.</summary>
    private void LogRenumbered(string why) =>
        _log.LogWarning("The PB Limiter profile queue numbers were reset, as {Reason}: waiting profiles keep their order, and what was last applied to each server is forgotten, so none is dropped over it", why);

    /// <summary>
    /// A file from an earlier version has no sequences. Its requests are numbered after any number the file holds, in the
    /// order of their queued times, the only order that version kept; ones queued together, at the same time, share a
    /// number as they would now. Its servers' last applied sequence stays 0, nothing known, so no request is dropped over a
    /// profile applied before the update: sending an old request once more is better than dropping a new one by a wrong
    /// clock. The counter moves past every number in the file, so a hand-edited file cannot have one handed out twice;
    /// <see cref="Normalise"/> has already reset numbers out of range, so this cannot overflow.
    /// </summary>
    private static void NumberUnsequenced(State state)
    {
        long last = Math.Max(state.LastSequence, 0);
        foreach (PendingApply pending in state.Pending)
            last = Math.Max(last, pending.Sequence);
        foreach (KnownServer known in state.KnownServers)
            last = Math.Max(last, known.LastAppliedSequence);

        foreach (IGrouping<DateTime, PendingApply> together in state.Pending.Where(p => p.Sequence <= 0).GroupBy(p => p.QueuedUtc).OrderBy(g => g.Key))
        {
            last++;
            foreach (PendingApply pending in together)
                pending.Sequence = last;
        }
        state.LastSequence = last;
    }

    private static List<T> WithoutNulls<T>(IEnumerable<T?>? items) where T : class => items?.OfType<T>().ToList() ?? new List<T>();

    private sealed class State
    {
        public List<SavedProfile> Profiles { get; set; } = new();
        public List<PendingApply> Pending { get; set; } = new();
        public List<AppliedProfile> Applied { get; set; } = new();
        public List<KnownServer> KnownServers { get; set; } = new();

        /// <summary>The newest <see cref="PendingApply.Sequence"/> handed out. Saved, so the order holds across restarts.</summary>
        public long LastSequence { get; set; }
    }

    /// <summary>
    /// The state one change works on. It reads the published state, and copies it only when the change first writes, so a
    /// change that finds nothing to do, as most of the queue's checks do, copies nothing. The copy's lists are new and
    /// their entries shared with the published state, which must never change: an entry is replaced in the copy by its
    /// own clone before it is edited, through <see cref="Edit(PendingApply)"/> and <see cref="Edit(KnownServer)"/>.
    /// </summary>
    private sealed class Draft
    {
        private readonly State _published;
        private State? _copy;

        // Entries this change already cloned, edited again without another clone.
        private HashSet<object>? _cloned;

        public Draft(State published) => _published = published;

        /// <summary>What the change sees: the published state, or its copy once the change has written.</summary>
        public State Read => _copy ?? _published;

        /// <summary>The copy to change. Entries are added, removed and replaced here; one is edited through Edit.</summary>
        public State Write => _copy ??= new State
        {
            Profiles = new List<SavedProfile>(_published.Profiles),
            Pending = new List<PendingApply>(_published.Pending),
            Applied = new List<AppliedProfile>(_published.Applied),
            KnownServers = new List<KnownServer>(_published.KnownServers),
            LastSequence = _published.LastSequence
        };

        /// <summary>The changed state, or null when the change wrote nothing.</summary>
        public State? Result => _copy;

        /// <summary>The copy's own clone of <paramref name="entry"/>, an entry read through <see cref="Read"/>, to edit.</summary>
        public PendingApply Edit(PendingApply entry) => Own(Write.Pending, entry, static p => p.Clone());

        /// <inheritdoc cref="Edit(PendingApply)"/>
        public KnownServer Edit(KnownServer entry) => Own(Write.KnownServers, entry, static s => s.Clone());

        private T Own<T>(List<T> list, T entry, Func<T, T> clone) where T : class
        {
            if (_cloned != null && _cloned.Contains(entry)) return entry;
            int index = list.IndexOf(entry);
            if (index < 0)
                throw new ArgumentException("The entry is not in this state.", nameof(entry));
            T own = clone(entry);
            list[index] = own;
            (_cloned ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(own);
            return own;
        }
    }
}
