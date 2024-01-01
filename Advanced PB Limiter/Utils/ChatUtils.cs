using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using VRage.Game;
using VRageMath;

namespace Advanced_PB_Limiter.Utils
{
    internal static class Chat
    {
        private const string Author = "PB Limiter";
        private static Color ChatColor = Color.Green;

        internal static void Send(string message, ulong Target, Color? color = null)
        {
            Color useColor = color ?? ChatColor;
            
            ScriptedChatMsg scripted = new ()
            {
                Author = Author,
                Text = message,
                Font = MyFontEnum.White,
                Color = useColor,
                Target = Sync.Players.TryGetIdentityId(Target)
            };

            MyMultiplayerBase.SendScriptedChatMessage(ref scripted);
        }
    }
    
}