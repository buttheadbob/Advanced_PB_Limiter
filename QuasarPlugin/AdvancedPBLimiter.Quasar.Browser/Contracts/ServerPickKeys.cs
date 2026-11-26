#nullable enable
using System;

namespace Advanced_PB_Limiter.QuasarContracts
{
    /// <summary>
    /// The stable key a browser page keeps a server selection by, and the worker resolves back to a known server. A
    /// cluster key names the slot, not an incarnation; a server key names one id. Both are safe in an HTML attribute and
    /// in JSON.
    /// </summary>
    public static class ServerPickKeys
    {
        public static string For(string? clusterId, string? clusterSlot, string? serverId)
        {
            return clusterId is { Length: > 0 } && clusterSlot is { Length: > 0 }
                ? "cluster:" + Uri.EscapeDataString(clusterId) + "|" + Uri.EscapeDataString(clusterSlot)
                : "server:" + Uri.EscapeDataString(serverId ?? "");
        }

        public static bool TryCluster(string? key, out string clusterId, out string clusterSlot)
        {
            clusterId = "";
            clusterSlot = "";
            if (key is not { Length: > 0 } || !key.StartsWith("cluster:", StringComparison.Ordinal)) return false;
            int split = key.IndexOf('|', "cluster:".Length);
            if (split < 0) return false;
            clusterId = Uri.UnescapeDataString(key.AsSpan("cluster:".Length, split - "cluster:".Length));
            clusterSlot = Uri.UnescapeDataString(key.AsSpan(split + 1));
            return clusterId.Length != 0 && clusterSlot.Length != 0;
        }

        public static string ServerIdOf(string? key)
        {
            return key is { Length: > 0 } && key.StartsWith("server:", StringComparison.Ordinal)
                ? Uri.UnescapeDataString(key.AsSpan("server:".Length))
                : "";
        }
    }
}
