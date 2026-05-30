using System.Threading.Tasks;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Sandbox;
using Sandbox.ModAPI.Ingame;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.Commands
{
    [Category("PBLimiter")]
    public class StaffCommands : CommandModule
    {
        private Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        [Command("requestpbshutdown", "Send the shutdown request to all pb scripts that monitor for it.  add the amount of seconds until restart if desired.  Eg: !requestpbshutdown 10")]
        [Permission(MyPromoteLevel.Admin)]
        public void RequestPbShutdown(int seconds = 1)
        {
            MySandboxGame.Static.Invoke(() => // Not sure if this actually needs to be forced on the game thread.
            {
                foreach (TrackedPlayer? tplayer in TrackingManager.PlayersTracked.Values)
                {
                    if (tplayer == null) continue;
                    foreach (TrackedPBBlock? pbBlock in tplayer.GetAllPBBlocks)
                    {
                        pbBlock?.ProgrammableBlock?.Run($"GracefulShutDown::{seconds}", UpdateType.Script);
                    }
                }
            }, "Advanced_PB_Limiter");
        }
        
        [Command("debug", "Only for debugging, not regular use for servers.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Debug(bool debug)
        {
            Config.DebugReporting = debug;
            Context.Respond("Debug mode is now " + (debug ? "enabled." : "disabled."));
        }
        
        [Command("GetReport", "Generates a full report of this server.")]
        [Permission(MyPromoteLevel.Admin)]
        public void GenerateReport()
        {
            Task.Run(()=> ReportManager.GetReport(Context)); // Fire and forget on another thread.
        }
        
        [Command("Disable", "Disables the Advanced PB Limiter.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Disable()
        {
            Config.Enabled = false;
            Context.Respond("Advanced PB Limiter is now disabled.");
        }
        
        [Command("Enable", "Enables the Advanced PB Limiter.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Enable()
        {
            Config.Enabled = true;
            Context.Respond("Advanced PB Limiter is now enabled.");
        }
        
        [Command("AllowStaffCommands", "Allows staff commands to be used. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void AllowStaffCommands()
        {
            Config.AllowStaffCommands = true;
            Advanced_PB_Limiter.Instance!.Save();
            Context.Respond("Staff commands are now enabled.");
        }
        
        [Command("DenyStaffCommands", "Disables staff commands from being used. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void DenyStaffCommands()
        {
            Config.AllowStaffCommands = false;
            Advanced_PB_Limiter.Instance!.Save();
            Context.Respond("Staff commands are now disabled.");
        }

        [Command("CreatePrivilegedUser", "Creates a new privileged user or overwrites an existing one.", "SteamId, MaxRunTimeMS, MaxRunTimeMSAvg, CombinedLimits, MaxAllBlocksCombinedRunTimeMS, MaxAllBlocksCombinedRunTimeMSAvg, OffencesBeforePunishment, Punishment, GracefulShutDownRequestDelay")]
        [Permission(MyPromoteLevel.Admin)]
        public void CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay)
        {
            if (!Config.AllowStaffCommands)
            {
                Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
                return;
            }
            
            if (Config.PrivilegedPlayers.ContainsKey(steamId))
            {
                Config.PrivilegedPlayers[steamId].RuntimeAllowance = maxRunTimeMS;
                Config.PrivilegedPlayers[steamId].RuntimeAverageAllowance = maxRunTimeMSAvg;
                Config.PrivilegedPlayers[steamId].NoCombinedLimits = !combinedLimits;
                Config.PrivilegedPlayers[steamId].CombinedRuntimeAllowance = maxAllBlocksCombinedRunTimeMS;
                Config.PrivilegedPlayers[steamId].CombinedRuntimeAverageAllowance = maxAllBlocksCombinedRunTimeMSAvg;
                Config.PrivilegedPlayers[steamId].OffencesBeforePunishment = offencesBeforePunishment;
                Config.PrivilegedPlayers[steamId].Punishment = punishment;
                Config.PrivilegedPlayers[steamId].GracefulShutDownRequestDelay = gracefulShutDownRequestDelay;
                Context.Respond("Privileged user updated.");
            }
            else
            {
                Config.PrivilegedPlayers.Add(steamId, new PrivilegedPlayer
                {
                    SteamId = steamId,
                    RuntimeAllowance = maxRunTimeMS,
                    RuntimeAverageAllowance = maxRunTimeMSAvg,
                    NoCombinedLimits = !combinedLimits,
                    CombinedRuntimeAllowance = maxAllBlocksCombinedRunTimeMS,
                    CombinedRuntimeAverageAllowance = maxAllBlocksCombinedRunTimeMSAvg,
                    OffencesBeforePunishment = offencesBeforePunishment,
                    Punishment = punishment,
                    GracefulShutDownRequestDelay = gracefulShutDownRequestDelay
                });
                Context.Respond("Privileged user created.");
            }

            Advanced_PB_Limiter.Instance!.Save();
        }

        [Command("DetailedAdminHelp", "Gives a list of all staff commands and their usage.")]
        [Permission(MyPromoteLevel.Admin)]
        public void DetailedHelp()
        {
            Context.Respond("Advanced PB Limiter Staff Commands:");
            Context.Respond("/PBLimiter GetReport - Generates a full report of this server.");
            Context.Respond("/PBLimiter Disable - Disables the Advanced PB Limiter.");
            Context.Respond("/PBLimiter Enable - Enables the Advanced PB Limiter.");
            Context.Respond("/PBLimiter AllowStaffCommands - Allows staff commands to be used. Only the server owner can use this command.");
            Context.Respond("/PBLimiter DenyStaffCommands - Disables staff commands from being used. Only the server owner can use this command.");
        }
        
        [Command("AdminHelp", "Gives a list of all staff commands.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Help()
        {
            Context.Respond("Advanced PB Limiter Staff Commands:");
            Context.Respond("/PBLimiter GetReport");
            Context.Respond("/PBLimiter Disable");
            Context.Respond("/PBLimiter Enable");
            Context.Respond("/PBLimiter AllowStaffCommands");
            Context.Respond("/PBLimiter DenyStaffCommands");
        }
    }
}
