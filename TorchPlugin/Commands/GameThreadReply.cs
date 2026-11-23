using System;
using Advanced_PB_Limiter.Utils;
using Sandbox;
using Torch.Commands;

namespace Advanced_PB_Limiter.Commands
{
    /// <summary>Replies for the reports built on another thread. Torch sends chat from the caller's thread, so they go to the game thread.</summary>
    internal static class GameThreadReply
    {
        private static readonly LimiterLog Log = new("Game Thread Reply");

        public static Action<string> For(CommandContext context)
        {
            return text =>
            {
                try
                {
                    MySandboxGame? game = MySandboxGame.Static;
                    if (game is null)
                    {
                        Respond(context, text); // No game to send it on, a console caller still gets it.
                        return;
                    }

                    game.Invoke(() => Respond(context, text), "Advanced_PB_Limiter");
                }
                catch (Exception e)
                {
                    // The reply runs in the game's update loop, where an exception is not caught: a session that ended
                    // while the report was building must not take the server down.
                    Log.Error(e, "Could not send the PB Limiter report reply");
                }
            };
        }

        private static void Respond(CommandContext context, string text)
        {
            try
            {
                context.Respond(text);
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not send the PB Limiter report reply");
            }
        }
    }
}
