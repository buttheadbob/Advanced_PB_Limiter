using System.Collections.Generic;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.Commands
{
    [Category("PBLimiter")]
    public class StaffCommands : CommandModule
    {
        private Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

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
        public async void PushConfig()
        {
            if (!Config.AllowStaffCommands)
            {
                Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
                return;
            }
            
            await NexusManager.UpdateNexusWithSettingsData();
            Context.Respond("Advanced PB Limiter config pushed to all Nexus servers.");
        }
        
        [Command("PushPrivilegedUserData", "Pushes all privileged user data to all Nexus servers.  This is a great way to sync new servers or update servers that were offline when a new privileged user was added.")]
        [Permission(MyPromoteLevel.Admin)]
        public async void PushPrivilegedUserData()
        {
            if (!Config.AllowStaffCommands)
            {
                Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
                return;
            }

            foreach (KeyValuePair<ulong, PrivilegedPlayer> privilegedPlayer in Config.PrivilegedPlayers)
            {
                await NexusManager.UpdateNexusWithPrivilegedPlayerData(privilegedPlayer.Value);
            }
            
            Context.Respond("Advanced PB Limiter privileged user data pushed to all Nexus servers.");
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
            Context.Respond("Advanced PB Limiter will not accept pushed privileged user data from Nexus.");
        }
        
        [Command("AllowStaffCommands", "Allows staff commands to be used. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void AllowStaffCommands()
        {
            Config.AllowStaffCommands = true;
            Context.Respond("Staff commands are now enabled.");
        }
        
        [Command("DenyStaffCommands", "Disables staff commands from being used. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void DenyStaffCommands()
        {
            Config.AllowStaffCommands = false;
            Context.Respond("Staff commands are now disabled.");
        }

        [Command("CreatePrivilegedUser", "Creates a new privileged user or overwrites an existing one.", "SteamId, MaxRunTimeMS, MaxRunTimeMSAvg, CombinedLimits, MaxAllBlocksCombinedRunTimeMS, MaxAllBlocksCombinedRunTimeMSAvg, OffencesBeforePunishment, Punishment, GracefulShutDownRequestDelay, UpdateNexus")]
        [Permission(MyPromoteLevel.Admin)]
        public async void CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay, bool updateNexus = false)
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
                await NexusManager.UpdateNexusWithPrivilegedPlayerData(Config.PrivilegedPlayers[steamId]);
            Context.Respond("Privileged user data pushed to all Nexus servers.");
        }

        [Command("DetailedHelp", "Gives a list of all staff commands and their usage.")]
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
        
        [Command("Help", "Gives a list of all staff commands.")]
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
