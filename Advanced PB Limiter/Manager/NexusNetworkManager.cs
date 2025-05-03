using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Advanced_PB_Limiter.Utils.Reports;
using Newtonsoft.Json;
using Nexus;
using NLog;
using ProtoBuf;
using Sandbox.ModAPI;

namespace Advanced_PB_Limiter.Manager
{
    public static class NexusNetworkManager
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static NexusVersion Nexus_Version = NexusVersion.None;
        public static NexusAPI.Server? ThisServer => Advanced_PB_Limiter.ThisServer;

        public enum NexusMessageType
        {
            PrivilegedPlayerData,
            Settings,
            RequestPlayerReports,
            PlayerReport
        }

        public static int ConnectedNexusServers => NexusAPI.GetAllServers().Count;
        
        public static void HandleReceivedMessage(ushort handlerId, byte[] data, ulong steamID, bool fromServer)
        {
            try
            {
                if (handlerId != 8697) return;
            
                NexusAPI.CrossServerMessage nMessage = MyAPIGateway.Utilities.SerializeFromBinary<NexusAPI.CrossServerMessage>(data);
                NexusMessage? message = MyAPIGateway.Utilities.SerializeFromBinary<NexusMessage>(nMessage.Message);
                if (message == null) return;
                if (message.FromServerID == ThisServer!.ServerID) return; // Don't process messages from itself.

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
            catch (Exception e)
            {
                Log.Error(e, "Error while handling nexus message.");
            }
        }

        private static void HandlePrivilegedPlayerData(byte[] data)
        {
            PrivilegedPlayer? privilegedPlayer = JsonConvert.DeserializeObject<PrivilegedPlayer>(Encoding.UTF8.GetString(data));
            
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
            
            Config.PrivilegedPlayers[privilegedPlayer.SteamId] = privilegedPlayer;
            Advanced_PB_Limiter.Instance!.Save();
        }
        
        private static void HandleUpdatedSettings(byte[] data)
        {
            try
            {
                Advanced_PB_LimiterConfig? newConfig = JsonConvert.DeserializeObject<Advanced_PB_LimiterConfig>(Encoding.UTF8.GetString(data));
            
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
            
                Advanced_PB_Limiter.Instance!.UpdateConfigFromNexus(newConfig);
            }
            catch (Exception e)
            {
                Log.Error(e, "Error while handling updated settings from nexus.");
            }
        }
        
        private static void HandlePlayerReports(int fromServer, byte[] data)
        {
            List<PlayerReport> playerReport = JsonConvert.DeserializeObject<List<PlayerReport>>(Encoding.UTF8.GetString(data));
            if (playerReport == null)
            {
                Log.Warn("Unable to deserialize player report data..");
                return;
            }
            
            ReportManager.AddOrUpdateReport(fromServer, playerReport);
        }

        public static bool UpdateNexusWithPrivilegedPlayerData(PrivilegedPlayer player)
        {
            if (ThisServer is null)
            {
                Log.Warn("Tries to send privileged player data to nexus, but this server is not configured properly.");
                return false; // IF not properly configured, this could happen.
            }

            byte[] playerData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(player));
            if (playerData == Array.Empty<byte>())
            {
                Log.Warn("Unable to serialize privileged player data to send to nexus.");
                return false;
            }
            
            NexusMessage message = new(ThisServer.ServerID, NexusMessageType.PrivilegedPlayerData, playerData);
            SendNexusMessage(message);
            
            return true;
        }

        public static bool UpdateNexusWithSettingsData()
        {
            if (ThisServer is null)
            {
                Log.Warn("Tries to send settings data to nexus, but this server is not configured properly.");
                return false; // IF not properly configured, this could happen.
            }
            
            byte[] settingsData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Config));
            if (settingsData == Array.Empty<byte>())
            {
                Log.Warn("Unable to serialize privileged player data to send to nexus.");
                return false;
            }
            
            NexusMessage message = new(ThisServer.ServerID, NexusMessageType.Settings, settingsData);
            SendNexusMessage(message);
            return true;
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
                        list[index].GetAllPBBlocks[ii].Offences.Count,
                        list[index].GetAllPBBlocks[ii].Recompiles);
                    
                    pbReports.Add(pbReport);
                }
                PlayerReport playerReport = new(list[index].SteamId, list[index].PlayerId, list[index].PlayerName ?? "Unknown", true, pbReports);

                playerReports.Add(playerReport);
            }

            byte[] FinishedReport = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(playerReports));
            if (FinishedReport == Array.Empty<byte>())
            {
                Log.Warn("Unable to serialize tracked player data to send through nexus.");
                return;
            }
            
            NexusMessage message = new(ThisServer!.ServerID, NexusMessageType.PlayerReport, FinishedReport);
            SendNexusMessage(message);
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
            SendNexusMessage(message);
            return Task.FromResult(true);
        }

        [ProtoContract]
        public sealed class NexusMessage
        {
            [ProtoMember(1)] public int FromServerID;
            [ProtoMember(2)] public NexusMessageType MessageType;
            [ProtoMember(3)] public byte[] MessageData;

            public NexusMessage(int _fromServerId, NexusMessageType _messageType, byte[] messageData)
            {
                FromServerID = _fromServerId;
                MessageType = _messageType;
                MessageData = messageData;
            }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
            public NexusMessage()
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
            {

            }
        }

        private static void SendNexusMessage(NexusMessage message)
        {
            if (!Advanced_PB_Limiter.NexusInstalled) return;
            
            byte[] data = MyAPIGateway.Utilities.SerializeToBinary(message);

            foreach (NexusAPI.Server server in NexusAPI.GetAllServers())
            {
                if (server.ServerID == ThisServer!.ServerID) continue;
                NexusAPI.CrossServerMessage nMessage = new(8697, server.ServerID, ThisServer!.ServerID, data);
                byte[] crossServerMessage = MyAPIGateway.Utilities.SerializeToBinary(nMessage);
                Advanced_PB_Limiter.Instance?.nexusAPI?.SendMessageToServer(server.ServerID, crossServerMessage);
            }
        }
    }
}