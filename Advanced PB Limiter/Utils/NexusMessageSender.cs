using System;
using System.Reflection;
using Advanced_PB_Limiter.Manager;
using HarmonyLib;
using Nexus.DataStructures;
using Nexus.PubSub;
using Sandbox.ModAPI;
using Torch.API.Plugins;
using Torch.Managers.PatchManager;

namespace Advanced_PB_Limiter.Utils
{
    public static class NexusMessageManager
    {
        private static PatchContext? ctx;
        private static Sockets? _socketInstance;
        
        public static void afterRun_init (ITorchPlugin nexusPlugin)
        {
            Type mainType = nexusPlugin.GetType();
            PropertyInfo? socketProperty = mainType.GetProperty("Socket", BindingFlags.Public | BindingFlags.Static);
            if (socketProperty != null)
            {
                _socketInstance = socketProperty.GetValue(null) as Sockets;
                
            }
        }
        
        public static void SendMessage(int toServer, byte[] message)
        {
            if (_socketInstance == null) return;
            Sockets.Publish(SubsriptionType.MessageType.ModAPI, new NexusAPI.CrossServerMessage(8697, toServer, NexusManager.ThisServer!.ServerID, message));
        }

        public static void SuffixMessageReceived(ref Message __result)
        {
            Message receivedMsg = __result;
            if (receivedMsg is null) return;
            if (receivedMsg.TopicType != 10250) return;
            var msg = MyAPIGateway.Utilities.SerializeFromBinary<NexusAPI.CrossServerMessage>(receivedMsg.Data);
            if (msg is null) return;
            if (msg.UniqueMessageID != 8697) return;
            NexusManager.HandleNexusMessage(0, msg.Message, 0, true);
            
        }
    }
    
    [HarmonyPatch(typeof(SubscriptionRecieved), "OnRecieve")]
    public static class SubscriptionReceived_OnRecieve_Patch
    {

        // The suffix method
        public static void Postfix(ref Message __result)
        {
            // Call your custom suffix method
            NexusMessageManager.SuffixMessageReceived(ref __result);
        }
    }
}