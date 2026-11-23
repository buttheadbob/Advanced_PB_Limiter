using System;
using Sandbox;
using Torch.Commands;

namespace Advanced_PB_Limiter.Commands
{
    /// <summary>Replies for the reports built on another thread. Torch sends chat from the caller's thread, so they go to the game thread.</summary>
    internal static class GameThreadReply
    {
        public static Action<string> For(CommandContext context)
        {
            return text =>
            {
                MySandboxGame? game = MySandboxGame.Static;
                if (game is null)
                    context.Respond(text); // No game to send it on, a console caller still gets it.
                else
                    game.Invoke(() => context.Respond(text), "Advanced_PB_Limiter");
            };
        }
    }
}
