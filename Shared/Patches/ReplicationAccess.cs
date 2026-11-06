using System;
using System.Collections;
using System.Reflection;
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
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo _clientStates = null!;
        private static FieldInfo _replicables = null!;
        private static MethodInfo _removeForClient = null!;
        private static MethodInfo _forceReplicable = null!;

        public static void Init()
        {
            Type server = typeof(MyReplicationServer);
            Type client = server.Assembly.GetType("VRage.Network.MyClient")
                ?? throw new InvalidOperationException("Could not find VRage.Network.MyClient, the game has probably changed");

            _clientStates = Required(server.GetField("m_clientStates", InstanceFlags), "MyReplicationServer.m_clientStates");
            _replicables = Required(client.GetField("Replicables", InstanceFlags), "MyClient.Replicables");
            _removeForClient = Required(server.GetMethod("RemoveForClient", InstanceFlags, null, new[] { typeof(IMyReplicable), client, typeof(bool) }, null), "MyReplicationServer.RemoveForClient");
            // ForceReplicable has a public overload as well, this is the one that targets a single client.
            _forceReplicable = Required(server.GetMethod("ForceReplicable", InstanceFlags, null, new[] { typeof(IMyReplicable), typeof(Endpoint) }, null), "MyReplicationServer.ForceReplicable");
        }

        public static IDictionary GetClientStates(MyReplicationServer server)
        {
            return (IDictionary)_clientStates.GetValue(server)!;
        }

        public static MyConcurrentDictionary<IMyReplicable, MyReplicableClientData> GetReplicables(object client)
        {
            return (MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>)_replicables.GetValue(client)!;
        }

        public static void ReSend(MyReplicationServer server, IMyReplicable replicable, object client, Endpoint endpoint)
        {
            _removeForClient.Invoke(server, new[] { replicable, client, true });
            _forceReplicable.Invoke(server, new object[] { replicable, endpoint });
        }

        private static T Required<T>(T? member, string name) where T : class
        {
            return member ?? throw new InvalidOperationException($"Could not find {name}, the game has probably changed");
        }
    }
}
