using System.Collections.Generic;
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
        
        [Command("GetReport", "Generates a full report of this server and any Nexus servers.")]
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
        
        [Command("PushConfig", "Pushes this plugin's config to all Nexus servers.  This is a great way to sync new servers or update servers that were offline during an update.")]
        [Permission(MyPromoteLevel.Admin)]
        public void PushConfig()
        {
            if (!Config.AllowStaffCommands)
            {
                Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
                return;
            }
            
            if (NexusNetworkManager.UpdateNexusWithSettingsData())
                Context.Respond("Advanced PB Limiter config pushed to all Nexus servers.");
            else
                Context.Respond("Advanced PB Limiter config failed to push to all Nexus servers.  See logs for more information.");
        }
        
        [Command("PushPrivilegedUserData", "Pushes all privileged user data to all Nexus servers.  This is a great way to sync new servers or update servers that were offline when a new privileged user was added.")]
        [Permission(MyPromoteLevel.Admin)]
        public void PushPrivilegedUserData()
        {
            if (!Config.AllowStaffCommands)
            {
                Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
                return;
            }

            bool pushSuccess = true;
            foreach (KeyValuePair<ulong, PrivilegedPlayer> privilegedPlayer in Config.PrivilegedPlayers)
            {
                if (!NexusNetworkManager.UpdateNexusWithPrivilegedPlayerData(privilegedPlayer.Value))
                    pushSuccess = false;
            }

            Context.Respond(pushSuccess
                ? "Advanced PB Limiter privileged user data pushed to all Nexus servers."
                : "Advanced PB Limiter privileged user data failed to push to all Nexus servers.  See logs for more information.");
        }
        
        [Command("AcceptPushedConfig", "Allows configuration data received through Nexus to override the config on this plugin.")]
        [Permission(MyPromoteLevel.Admin)]
        public void AcceptPushedConfig()
        {
            if (!Config.AllowStaffCommands)
            {
                Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
                return;
            }

            Config.AllowNexusConfigUpdates = true;
            Advanced_PB_Limiter.Instance!.Save();
            Context.Respond("Advanced PB Limiter will now accept pushed config data from Nexus.");
        }
        
        [Command("DenyPushedConfig", "Disables this plugin's config from being overwritten by any config data received through nexus.")]
        [Permission(MyPromoteLevel.Admin)]
        public void DenyPushedConfig()
        {
            if (!Config.AllowStaffCommands)
            {
                Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
                return;
            }

            Config.AllowNexusConfigUpdates = false;
            Advanced_PB_Limiter.Instance!.Save();
            Context.Respond("Advanced PB Limiter will now accept pushed config data from Nexus.");
        }
        
        [Command("AcceptPushedPrivilegedUserData", "Allows privileged user data received through Nexus to override the privileged user data on this plugin.  This will not remove player data, only update or add new data.")]
        [Permission(MyPromoteLevel.Admin)]
        public void AcceptPushedPrivilegedUserData()
        {
            if (!Config.AllowStaffCommands)
            {
                Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
                return;
            }

            Config.AllowNexusPrivilegedPlayerUpdates = true;
            Advanced_PB_Limiter.Instance!.Save();
            Context.Respond("Advanced PB Limiter will now accept pushed privileged user data from Nexus.");
        }
        
        [Command("DenyPushedPrivilegedUserData", "Disables this plugin's privileged user data from being overwritten by any privileged user data received through nexus.")]
        [Permission(MyPromoteLevel.Admin)]
        public void DenyPushedPrivilegedUserData()
        {
            if (!Config.AllowStaffCommands)
            {
                Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
                return;
            }

            Config.AllowNexusPrivilegedPlayerUpdates = false;
            Advanced_PB_Limiter.Instance!.Save();
            Context.Respond("Advanced PB Limiter will not accept pushed privileged user data from Nexus.");
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

        [Command("CreatePrivilegedUser", "Creates a new privileged user or overwrites an existing one.", "SteamId, MaxRunTimeMS, MaxRunTimeMSAvg, CombinedLimits, MaxAllBlocksCombinedRunTimeMS, MaxAllBlocksCombinedRunTimeMSAvg, OffencesBeforePunishment, Punishment, GracefulShutDownRequestDelay, UpdateNexus")]
        [Permission(MyPromoteLevel.Admin)]
        public void CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay, bool updateNexus = false)
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

            if (updateNexus)
                NexusNetworkManager.UpdateNexusWithPrivilegedPlayerData(Config.PrivilegedPlayers[steamId]);
            Advanced_PB_Limiter.Instance!.Save();
            Context.Respond("Privileged user data pushed to all Nexus servers.");
        }

        [Command("DetailedAdminHelp", "Gives a list of all staff commands and their usage.")]
        [Permission(MyPromoteLevel.Admin)]
        public void DetailedHelp()
        {
            Context.Respond("Advanced PB Limiter Staff Commands:");
            Context.Respond("/PBLimiter GetReport - Generates a full report of this server and any Nexus servers.");
            Context.Respond("/PBLimiter Disable - Disables the Advanced PB Limiter.");
            Context.Respond("/PBLimiter Enable - Enables the Advanced PB Limiter.");
            Context.Respond("/PBLimiter PushConfig - Pushes this plugin's config to all Nexus servers.  This is a great way to sync new servers or update servers that were offline during an update.");
            Context.Respond("/PBLimiter PushPrivilegedUserData - Pushes all privileged user data to all Nexus servers.  This is a great way to sync new servers or update servers that were offline when a new privileged user was added.");
            Context.Respond("/PBLimiter AcceptPushedConfig - Allows configuration data received through Nexus to override the config on this plugin.");
            Context.Respond("/PBLimiter DenyPushedConfig - Disables this plugin's config from being overwritten by any config data received through nexus.");
            Context.Respond("/PBLimiter AcceptPushedPrivilegedUserData - Allows privileged user data received through Nexus to override the privileged user data on this plugin.  This will not remove player data, only update or add new data.");
            Context.Respond("/PBLimiter DenyPushedPrivilegedUserData - Disables this plugin's privileged user data from being overwritten by any privileged user data received through nexus.");
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
            Context.Respond("/PBLimiter PushConfig");
            Context.Respond("/PBLimiter PushPrivilegedUserData");
            Context.Respond("/PBLimiter AcceptPushedConfig");
            Context.Respond("/PBLimiter DenyPushedConfig");
            Context.Respond("/PBLimiter AcceptPushedPrivilegedUserData");
            Context.Respond("/PBLimiter DenyPushedPrivilegedUserData");
            Context.Respond("/PBLimiter AllowStaffCommands");
            Context.Respond("/PBLimiter DenyStaffCommands");
        }
    }
}
