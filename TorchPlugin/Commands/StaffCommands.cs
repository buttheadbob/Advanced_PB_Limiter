using Advanced_PB_Limiter.Utils;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.Commands
{
    [Category("PBLimiter")]
    public class StaffCommands : CommandModule
    {
        [Command("requestpbshutdown", "Sends the shutdown request to all programmable block scripts that monitor for it, with the seconds until restart, 1 if left out. Eg: !PBLimiter requestpbshutdown 10")]
        [Permission(MyPromoteLevel.Admin)]
        public void RequestPbShutdown(int seconds = 1)
        {
            Context.Respond(LimiterCommands.RequestPbShutdown(seconds));
        }

        [Command("debug", "Only for debugging, not regular use for servers.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Debug(bool debug)
        {
            LimiterCommands.SetDebug(debug, GameThreadReply.For(Context));
        }

        [Command("GetReport", "Generates a full report of this server.")]
        [Permission(MyPromoteLevel.Admin)]
        public void GenerateReport()
        {
            LimiterCommands.GetReport(GameThreadReply.For(Context));
        }

        [Command("Disable", "Disables the Advanced PB Limiter.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Disable()
        {
            LimiterCommands.SetEnabled(false, GameThreadReply.For(Context));
        }

        [Command("Enable", "Enables the Advanced PB Limiter.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Enable()
        {
            LimiterCommands.SetEnabled(true, GameThreadReply.For(Context));
        }

        [Command("AllowStaffCommands", "Allows staff to use CreatePrivilegedUser. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void AllowStaffCommands()
        {
            LimiterCommands.SetStaffCommandsAllowed(true, GameThreadReply.For(Context));
        }

        [Command("DenyStaffCommands", "Stops staff using CreatePrivilegedUser. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void DenyStaffCommands()
        {
            LimiterCommands.SetStaffCommandsAllowed(false, GameThreadReply.For(Context));
        }

        [Command("CreatePrivilegedUser", "Creates a new privileged user or overwrites an existing one.", "SteamId, MaxRunTimeMS, MaxRunTimeMSAvg, CombinedLimits, MaxAllBlocksCombinedRunTimeMS, MaxAllBlocksCombinedRunTimeMSAvg, OffencesBeforePunishment, Punishment, GracefulShutDownRequestDelay")]
        [Permission(MyPromoteLevel.Admin)]
        public void CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay)
        {
            LimiterCommands.CreatePrivilegedUser(steamId, maxRunTimeMS, maxRunTimeMSAvg, combinedLimits, maxAllBlocksCombinedRunTimeMS, maxAllBlocksCombinedRunTimeMSAvg, offencesBeforePunishment, punishment, gracefulShutDownRequestDelay, GameThreadReply.For(Context));
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
