using System;
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
    internal static class NexusManager
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        internal static NexusAPI.Server? ThisServer { get; private set; }
        internal delegate void NexusConnectedDelegate(object? sender, NexusConnectedEventArgs e);
        
        
        internal enum NexusMessageType
        {
            PrivilegedPlayerData,
            Settings,
            RequestPlayerReports,
            PlayerReport
        }
        
        internal static void SetServerData(NexusAPI.Server server)
        {
            ThisServer = server;
        }
        
        internal static int ConnectedNexusServers => NexusAPI.GetAllServers().Count;
        
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
                    HandlePlayerReports(message.FromServerID, message.MessageData);
                    break;
            }
        }

        private static void HandlePrivilegedPlayerData(byte[] data)
        {
            PrivilegedPlayer? privilegedPlayer = MySerializer.DeserializeFromByteArray<PrivilegedPlayer>(data);
            if (privilegedPlayer == null || privilegedPlayer.SteamId == 0)
            {
                Log.Warn("Received null or invalid privileged player data from nexus, ignoring it.");
                return;
            }

            if (!Config.AllowNexusPrivilegedPlayerUpdates)
            {
                Log.Info("Received privileged player data from nexus, this server is set to not update privileged data from Nexus.");
                return;
            }
            
            if (Config.PrivilegedPlayers.ContainsKey(privilegedPlayer.SteamId))
                Config.PrivilegedPlayers[privilegedPlayer.SteamId] = privilegedPlayer;
            else
                Config.PrivilegedPlayers.TryAdd(privilegedPlayer.SteamId, privilegedPlayer);
        }
        
        private static async void HandleUpdatedSettings(byte[] data)
        {
            Advanced_PB_LimiterConfig? newConfig = MySerializer.DeserializeFromByteArray<Advanced_PB_LimiterConfig>(data);
            if (newConfig == null)
            {
                Log.Warn("Received null or invalid settings data from nexus, ignoring it.");
                return;
            }
            
            if (!Config.AllowNexusConfigUpdates)
            {
                Log.Info("Received settings data from nexus, this server is set to not update from Nexus.");
                return;
            }
            
            await Advanced_PB_Limiter.Instance!.UpdateConfigFromNexus(newConfig);
        }
        
        internal static List<NexusAPI.Server> GetNexusServers()
        {
            return NexusAPI.GetAllServers();
        }
        
        internal static NexusAPI.Server? GetServerFromId(int serverId)
        {
            for (int index = 0; index < NexusAPI.GetAllServers().Count; index++)
            {
                NexusAPI.Server server = NexusAPI.GetAllServers()[index];
                if (server.ServerID == serverId)
                    return server;
            }

            return null;
        }

        private static void HandlePlayerReports(int fromServer, byte[] data)
        {
            List<PlayerReport>? playerReport = MySerializer.DeserializeFromByteArray<List<PlayerReport>>(data);
            if (playerReport == null)
            {
                Log.Warn("Unable to deserialize player report data..");
                return;
            }
            
            ReportManager.AddOrUpdateReport(fromServer, playerReport);
        }

        internal static Task<bool> UpdateNexusWithPrivilegedPlayerData(PrivilegedPlayer player)
        {
            if (ThisServer is null)
            {
                Log.Warn("Tries to send privileged player data to nexus, but this server is not configured properly.");
                return Task.FromResult(false); // IF not properly configured, this could happen.
            }
            
            byte[]? playerData = MySerializer.SerializeToByteArray(player);
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

        internal static Task<bool> UpdateNexusWithSettingsData()
        {
            if (ThisServer is null)
            {
                Log.Warn("Tries to send settings data to nexus, but this server is not configured properly.");
                return Task.FromResult(false); // IF not properly configured, this could happen.
            }
            
            byte[]? settingsData = MySerializer.SerializeToByteArray(Config);
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

        private static void HandlePlayerReportRequest(int fromServer)
        {
            List<PlayerReport> playerReports = new ();

            List<TrackedPlayer> list = TrackingManager.GetTrackedPlayerData();
            for (int index = 0; index < list.Count; index++)
            {
                List<PBReport> pbReports = new();
                for (int ii = 0; ii < list[index].GetAllPBBlocks.Count; ii++)
                {
                    if (list[index].GetAllPBBlocks[ii] is null || list[index].GetAllPBBlocks[ii].ProgrammableBlock == null) continue;
                    
                    PBReport pbReport = new(list[index].SteamId,
                        list[index].GetAllPBBlocks[ii].ProgrammableBlock!.SlimBlock.CubeGrid.DisplayName,
                        list[index].GetAllPBBlocks[ii].ProgrammableBlock!.DisplayName,
                        ThisServer!.ServerID,
                        list[index].GetAllPBBlocks[ii].LastRunTimeMS,
                        list[index].GetAllPBBlocks[ii].RunTimeMSAvg,
                        list[index].GetAllPBBlocks[ii].Offences,
                        list[index].GetAllPBBlocks[ii].Recompiles);
                    
                    pbReports.Add(pbReport);
                }
                PlayerReport playerReport = new(list[index].SteamId, list[index].PlayerId, list[index].PlayerName, true, pbReports);

                playerReports.Add(playerReport);
            }

            byte[]? FinishedReport = MySerializer.SerializeToByteArray(playerReports);
            if (FinishedReport == null)
            {
                Log.Warn("Unable to serialize tracked player data to send through nexus.");
                return;
            }
            
            NexusMessage message = new(ThisServer!.ServerID, NexusMessageType.PlayerReport, FinishedReport);
            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(message);
            Advanced_PB_Limiter.nexusAPI?.SendMessageToServer(fromServer, data);
        }
        
        internal static Task<bool> RequestPlayerReports()
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
        
        internal static event NexusConnectedDelegate? NexusConnected;
        internal static void RaiseNexusConnectedEvent(bool connected)
        {
            NexusConnectedEventArgs args = new (connected);
            NexusConnected?.Invoke(null, args);
        }
    }

    internal sealed class NexusMessage
    {
        internal readonly int FromServerID;
        internal readonly NexusManager.NexusMessageType MessageType;
        internal readonly byte[] MessageData;

        internal NexusMessage(int _fromServerId, NexusManager.NexusMessageType _messageType, byte[] messageData)
        {
            FromServerID = _fromServerId;
            MessageType = _messageType;
            MessageData = messageData;
        }
    }

    internal static class MySerializer
    {
        internal static byte[]? SerializeToByteArray(object? obj)
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
        
        internal static T? DeserializeFromByteArray<T>(byte[]? data)
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
    
    internal class NexusConnectedEventArgs : EventArgs
    {
        public bool Connected { get; set; }

        public NexusConnectedEventArgs(bool connected)
        {
            Connected = connected;
        }
    }
}