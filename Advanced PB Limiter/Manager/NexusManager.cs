using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox.ModAPI;

namespace Advanced_PB_Limiter.Manager
{
    public static class NexusManager
    {
        private static Advanced_PB_LimiterConfig? Config => Advanced_PB_Limiter.Instance?.Config;
        private static Logger Log = LogManager.GetCurrentClassLogger();
        private static NexusAPI.Server? ThisServer;
        public enum NexusMessageType
        {
            PrivilegedPlayerData,
            Settings,
            RequestPlayerReports,
            PlayerReport
        }
        
        public static void SetServerData(NexusAPI.Server server)
        {
            ThisServer = server;
        }
        
        internal static void HandleNexusMessage(ushort handlerId, byte[] data, ulong steamID, bool fromServer)
        {
            NexusMessage? message = MyAPIGateway.Utilities.SerializeFromBinary<NexusMessage>(data);
            if (message == null) return;
            if (message.FromServerID == ThisServer!.ServerID) return; // Don't process messages from this server.

            switch (message.MessageType)
            {
                case NexusMessageType.PrivilegedPlayerData:
                    HandlePrivilegedPlayerData(message.MessageData);
                    break;
                case NexusMessageType.Settings:
                    HandleUpdatedSettings(message.MessageData);
                    break;
                case NexusMessageType.RequestPlayerReports:
                    HandlePlayerReportRequest(message.FromServerID);
                    break;
                case NexusMessageType.PlayerReport:
                    break;
            }
        }

        private static void HandlePrivilegedPlayerData(byte[] data)
        {
            PrivilegedPlayer? privilegedPlayer = Converter.DeserializeFromByteArray<PrivilegedPlayer>(data);
            if (privilegedPlayer == null || privilegedPlayer.SteamId == 0) return;
            
            if (Config!.PrivilegedPlayers.ContainsKey(privilegedPlayer.SteamId))
                Config.PrivilegedPlayers[privilegedPlayer.SteamId] = privilegedPlayer;
            else
                Config.PrivilegedPlayers.TryAdd(privilegedPlayer.SteamId, privilegedPlayer);
        }
        
        private static void HandleUpdatedSettings(byte[] data)
        {
            Advanced_PB_LimiterConfig? newConfig = Converter.DeserializeFromByteArray<Advanced_PB_LimiterConfig>(data);
            if (newConfig == null) return;
            
            Advanced_PB_Limiter.Instance?.UpdateConfigFromNexus(newConfig);
        }

        private static void HandlePlayerReport(byte[] data)
        {
            // Need to do this shit still...
            PlayerReport? playerReport = Converter.DeserializeFromByteArray<PlayerReport>(data);
            if (playerReport == null) return;
        }

        public static Task<bool> UpdateNexusWithPrivilegedPlayerData(PrivilegedPlayer player)
        {
            if (ThisServer is null)
            {
                Log.Warn("Tries to send privileged player data to nexus, but this server is not configured properly.");
                return Task.FromResult(false); // IF not properly configured, this could happen.
            }
            
            byte[]? playerData = Converter.SerializeToByteArray(player);
            if (playerData == null)
            {
                Log.Warn("Unable to serialize privileged player data to send to nexus.");
                return Task.FromResult(false);
            }

            NexusMessage message = new(ThisServer.ServerID, NexusMessageType.PrivilegedPlayerData, playerData);
            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(message);
            
            foreach (NexusAPI.Server? server in NexusAPI.GetAllServers())
            {
                Advanced_PB_Limiter.nexusAPI?.SendMessageToServer(server.ServerID, data);
            }
            
            return Task.FromResult(true);
        }

        public static Task<bool> UpdateNexusWithSettingsData()
        {
            if (ThisServer is null)
            {
                Log.Warn("Tries to send settings data to nexus, but this server is not configured properly.");
                return Task.FromResult(false); // IF not properly configured, this could happen.
            }
            
            byte[]? settingsData = Converter.SerializeToByteArray(Config);
            if (settingsData == null)
            {
                Log.Warn("Unable to serialize privileged player data to send to nexus.");
                return Task.FromResult(false);
            }
            
            NexusMessage message = new(ThisServer.ServerID, NexusMessageType.Settings, settingsData);
            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(message);
            
            foreach (NexusAPI.Server? server in NexusAPI.GetAllServers())
            {
                Advanced_PB_Limiter.nexusAPI?.SendMessageToServer(server.ServerID, data);
            }
            
            return Task.FromResult(true);
        }

        private static bool HandlePlayerReportRequest(int fromServer)
        {
            byte[]? trackedPlayersData = Converter.SerializeToByteArray(TrackingManager.GetTrackedPlayerData());
            if (trackedPlayersData == null)
            {
                Log.Warn("Unable to serialize tracked player data to send to nexus.");
                return false;
            }
            
            NexusMessage message = new(ThisServer!.ServerID, NexusMessageType.PlayerReport, trackedPlayersData);
            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(message);
            Advanced_PB_Limiter.nexusAPI?.SendMessageToServer(fromServer, data);
            return true;
        }
        
        public static Task<bool> RequestPlayerReports()
        {
            if (ThisServer is null)
            {
                Log.Warn("Tried to send player report request data to nexus, but this server is not configured properly.");
                return Task.FromResult(false); // IF not properly configured, this could happen.
            }

            byte[] requesterData = { 0 }; // So were not throwing null shit into protobuf and nexus.
            
            NexusMessage message = new(ThisServer.ServerID, NexusMessageType.RequestPlayerReports, requesterData);
            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(message);
            
            foreach (NexusAPI.Server? server in NexusAPI.GetAllServers())
            {
                Advanced_PB_Limiter.nexusAPI?.SendMessageToServer(server.ServerID, data);
            }
            
            return Task.FromResult(true);
        }
    }

    public sealed class NexusMessage
    {
        public readonly int FromServerID;
        public readonly NexusManager.NexusMessageType MessageType;
        public readonly byte[] MessageData;

        public NexusMessage(int _fromServerId, NexusManager.NexusMessageType _messageType, byte[] messageData)
        {
            FromServerID = _fromServerId;
            MessageType = _messageType;
            MessageData = messageData;
        }
    }

    public static class Converter
    {
        public static byte[]? SerializeToByteArray(object? obj)
        {
            if (obj == null)
            {
                return null;
            }

            BinaryFormatter bf = new ();
            using MemoryStream ms = new ();
            bf.Serialize(ms, obj);
            return ms.ToArray();
        }
        
        public static T? DeserializeFromByteArray<T>(byte[]? data)
        {
            if (data == null)
            {
                return default;
            }

            using MemoryStream ms = new (data);
            BinaryFormatter bf = new ();
            object obj = bf.Deserialize(ms);
            return (T)obj;
        }
    }
}