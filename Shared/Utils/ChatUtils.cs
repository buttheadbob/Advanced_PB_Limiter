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

        /// <param name="message">The message. Nothing is sent when it is empty.</param>
        /// <param name="identityId">Who gets it. Clients treat a target of 0 as everyone, so 0 sends nothing.</param>
        /// <param name="color">The message colour, green when left out.</param>
        public static void Send(string message, long identityId, Color? color = null)
        {
            if (identityId == 0 || string.IsNullOrEmpty(message)) return;

            ScriptedChatMsg scripted = new ()
            {
                Author = Author,
                Text = message,
                Font = MyFontEnum.White,
                Color = color ?? ChatColor,
                Target = identityId
            };

            MyMultiplayerBase.SendScriptedChatMessage(ref scripted);
        }
    }
}
