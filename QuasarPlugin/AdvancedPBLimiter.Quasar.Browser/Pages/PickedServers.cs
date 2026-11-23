using Advanced_PB_Limiter.QuasarContracts;

namespace AdvancedPBLimiter.Quasar.Pages;

/// <summary>
/// The servers ticked in a ServerPicker. A tick is kept by <see cref="ServerInfo.PickKey"/>, so it follows a cluster node
/// that restarts in the same slot under a new id; the worker resolves the slot when it queues.
/// </summary>
public sealed class PickedServers
{
    /// <summary>The ticked keys, the set the ServerPicker edits.</summary>
    public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);

    /// <summary>Drops ticks for servers that are no longer known, and for a cluster slot that is gone.</summary>
    public void Keep(IEnumerable<ServerInfo> servers)
    {
        HashSet<string> known = servers.Select(s => s.PickKey).ToHashSet(StringComparer.Ordinal);
        Ids.RemoveWhere(id => !known.Contains(id));
    }

    public void Deselect(ServerInfo server) => Ids.Remove(server.PickKey);

    public void Clear() => Ids.Clear();
}
