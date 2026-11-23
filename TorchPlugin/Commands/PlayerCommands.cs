using System.Text;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.Commands
{
    [Category("PBLimiter")]
    public class PlayerCommands : CommandModule
    {
        [Command("About", "Displays information about the Advanced PB Limiter.")]
        [Permission(MyPromoteLevel.None)]
        public void About()
        {
            foreach (string line in LimiterCommands.About())
                Context.Respond(line);
        }

        [Command("MyStats", "Generates a report about your programmable blocks for this server.")]
        [Permission(MyPromoteLevel.None)]
        public void GenerateReport()
        {
            if (Context.Player == null)
            {
                Context.Respond(LimiterCommands.PlayersOnlyMessage);
                return;
            }
            CommandContext context = Context;
            LimiterCommands.MyStats(context.Player.SteamUserId, GameThreadReply.For(context));
        }

        [Command("Limits", "Shows all limits applied to programmable blocks for this server.")]
        [Permission(MyPromoteLevel.None)]
        public void ShowLimits()
        {
            Context.Respond(LimiterCommands.Limits(Context.Player?.SteamUserId));
        }

        [Command("Runtimes", "Shows the last saved runtimes of up to 10 of the player's programmable blocks, once every 30 seconds.")]
        [Permission(MyPromoteLevel.None)]
        public void Runtimes()
        {
            if (Context.Player == null)
            {
                Context.Respond(LimiterCommands.PlayersOnlyMessage);
                return;
            }
            CommandContext context = Context;
            LimiterCommands.Runtimes(context.Player.IdentityId, GameThreadReply.For(context));
        }

        [Command("Help", "Shows player commands.")]
        [Permission(MyPromoteLevel.None)]
        public void PlayerHelp()
        {
            StringBuilder sb = new();
            sb.AppendLine();
            sb.AppendLine("Advanced PB Limiter Player Commands");
            sb.AppendLine("------------------------------------");
            sb.AppendLine("Limits - Shows your limits applied to programmable blocks for this server.");
            sb.AppendLine("MyStats - Generates a report about your programmable blocks for this server.");
            sb.AppendLine("Runtimes - Shows the last saved runtimes of up to 10 of your programmable blocks, once every 30 seconds.");
            sb.AppendLine();
            sb.AppendLine("About - Displays information about the Advanced PB Limiter.");
            Context.Respond(sb.ToString());
        }
    }
}
