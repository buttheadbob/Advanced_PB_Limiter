using Advanced_PB_Limiter.Utils;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.Commands
{
    [Category("PBLimiter")]
    public class StaffCommands : CommandModule
    {
        [Command("requestpbshutdown", "Send the shutdown request to all pb scripts that monitor for it.  add the amount of seconds until restart if desired.  Eg: !requestpbshutdown 10")]
        [Permission(MyPromoteLevel.Admin)]
        public void RequestPbShutdown(int seconds = 1)
        {
            LimiterCommands.RequestPbShutdown(seconds);
        }

        [Command("debug", "Only for debugging, not regular use for servers.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Debug(bool debug)
        {
            Context.Respond(LimiterCommands.SetDebug(debug));
        }

        [Command("GetReport", "Generates a full report of this server.")]
        [Permission(MyPromoteLevel.Admin)]
        public void GenerateReport()
        {
            CommandContext context = Context;
            LimiterCommands.GetReport(text => context.Respond(text));
        }

        [Command("Disable", "Disables the Advanced PB Limiter.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Disable()
        {
            Context.Respond(LimiterCommands.SetEnabled(false));
        }

        [Command("Enable", "Enables the Advanced PB Limiter.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Enable()
        {
            Context.Respond(LimiterCommands.SetEnabled(true));
        }

        [Command("AllowStaffCommands", "Allows staff commands to be used. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void AllowStaffCommands()
        {
            Context.Respond(LimiterCommands.SetStaffCommandsAllowed(true));
        }

        [Command("DenyStaffCommands", "Disables staff commands from being used. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void DenyStaffCommands()
        {
            Context.Respond(LimiterCommands.SetStaffCommandsAllowed(false));
        }

        [Command("CreatePrivilegedUser", "Creates a new privileged user or overwrites an existing one.", "SteamId, MaxRunTimeMS, MaxRunTimeMSAvg, CombinedLimits, MaxAllBlocksCombinedRunTimeMS, MaxAllBlocksCombinedRunTimeMSAvg, OffencesBeforePunishment, Punishment, GracefulShutDownRequestDelay")]
        [Permission(MyPromoteLevel.Admin)]
        public void CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay)
        {
            Context.Respond(LimiterCommands.CreatePrivilegedUser(steamId, maxRunTimeMS, maxRunTimeMSAvg, combinedLimits, maxAllBlocksCombinedRunTimeMS, maxAllBlocksCombinedRunTimeMSAvg, offencesBeforePunishment, punishment, gracefulShutDownRequestDelay));
        }

        [Command("DetailedAdminHelp", "Gives a list of all staff commands and their usage.")]
        [Permission(MyPromoteLevel.Admin)]
        public void DetailedHelp()
        {
            foreach (string line in LimiterCommands.DetailedStaffHelp())
                Context.Respond(line);
        }

        [Command("AdminHelp", "Gives a list of all staff commands.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Help()
        {
            foreach (string line in LimiterCommands.StaffHelp())
                Context.Respond(line);
        }
    }
}
