using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using VRage.Game;
using VRageMath;

namespace Advanced_PB_Limiter.Utils
{
    public static class Chat
    {
        private const string Author = "Advanced PB Limiter";
        private static readonly Color ChatColor = Color.Green;

        public static void Send(string message, ulong Target, Color? color = null)
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