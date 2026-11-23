#nullable enable
using System;
using System.Collections;
using System.Reflection;
using Advanced_PB_Limiter.Utils;
using VRage.Collections;
using VRage.Network;
using VRage.Replication;

namespace Advanced_PB_Limiter.Patches
{
    /// <summary>
    /// The private replication members used to re-send a dismounted block to players. Reflection is fine here,
    /// this only runs when a protected block is punished. MyClient is internal, so it can only be held as object.
    /// </summary>
    internal static class ReplicationAccess
    {
        private static FieldInfo? _clientStates;
        private static FieldInfo? _replicables;
        private static MethodInfo? _removeForClient;
        private static MethodInfo? _forceReplicable;

        public static void Init()
        {
            Type server = typeof(MyReplicationServer);
            Type client = server.Assembly.GetType("VRage.Network.MyClient")
                ?? throw new InvalidOperationException("Could not find VRage.Network.MyClient, the game has probably changed");

            _clientStates = GameMembers.Field(server, "m_clientStates");
            _replicables = GameMembers.Field(client, "Replicables");
            _removeForClient = GameMembers.Method(server, "RemoveForClient", typeof(IMyReplicable), client, typeof(bool));
            // ForceReplicable has a public overload as well, this is the one that targets a single client.
            _forceReplicable = GameMembers.Method(server, "ForceReplicable", typeof(IMyReplicable), typeof(Endpoint));
        }

        public static IDictionary GetClientStates(MyReplicationServer server)
        {
            return GameMembers.Ready(_clientStates).GetValue(server) as IDictionary
                   ?? throw new InvalidOperationException("MyReplicationServer has no client states.");
        }

        public static MyConcurrentDictionary<IMyReplicable, MyReplicableClientData> GetReplicables(object client)
        {
            return GameMembers.Ready(_replicables).GetValue(client) as MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>
                   ?? throw new InvalidOperationException("A replication client has no replicables.");
        }

        public static void ReSend(MyReplicationServer server, IMyReplicable replicable, object client, Endpoint endpoint)
        {
            GameMembers.Ready(_removeForClient).Invoke(server, new[] { replicable, client, true });
            GameMembers.Ready(_forceReplicable).Invoke(server, new object[] { replicable, endpoint });
        }
    }
}
