using System;
using System.Collections.Generic;
using Advanced_PB_Limiter.Commands;
using Advanced_PB_Limiter.Utils;
using PluginSdk.Commands;
using Sandbox;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>
/// Magnetar wrapper with the same commands and levels as the Torch plugin, under !PBLimiter.
/// The host generates !PBLimiter help, which is why the Torch Help command has no match here.
/// </summary>
[CommandRoot("PBLimiter", "Advanced PB Limiter", "limit programmable block run times")]
public sealed class PbLimiterCommands : CommandModule
{
    // The player reports are built on another thread and replies belong on the game thread.
    private Action<string> RespondOnGameThread()
    {
        CommandContext context = Context;
        return text => MySandboxGame.Static.Invoke(() => context.Respond(text), "Advanced_PB_Limiter");
    }

    [Command("About", "Displays information about the Advanced PB Limiter.")]
    [Permission(MyPromoteLevel.None)]
    public IEnumerable<string> About() => LimiterCommands.About();

    [Command("MyStats", "Generates a report about your programmable blocks for this server.")]
    [Permission(MyPromoteLevel.None)]
    public void MyStats()
    {
        if (Context.Caller.IsConsole)
        {
            Context.Respond(LimiterCommands.PlayersOnlyMessage);
            return;
        }
        LimiterCommands.MyStats(Context.Caller.SteamId, RespondOnGameThread());
    }

    [Command("Limits", "Shows all limits applied to programmable blocks for this server.")]
    [Permission(MyPromoteLevel.None)]
    public string Limits() => LimiterCommands.Limits(Context.Caller.IsConsole ? null : Context.Caller.SteamId);

    [Command("Runtimes", "Shows the last saved runtimes for each programmable block owned by the player.")]
    [Permission(MyPromoteLevel.None)]
    public void Runtimes()
    {
        if (Context.Caller.IsConsole)
        {
            Context.Respond(LimiterCommands.PlayersOnlyMessage);
            return;
        }
        LimiterCommands.Runtimes(Context.Caller.IdentityId, RespondOnGameThread());
    }

    [Command("requestpbshutdown", "Send the shutdown request to all pb scripts that monitor for it.  add the amount of seconds until restart if desired.  Eg: !PBLimiter requestpbshutdown 10")]
    [Permission(MyPromoteLevel.Admin)]
    public string RequestPbShutdown(int seconds = 1) => LimiterCommands.RequestPbShutdown(seconds);

    [Command("debug", "Only for debugging, not regular use for servers.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Debug(bool debug) => LimiterCommands.SetDebug(debug);

    [Command("Timings", "Logs the limiter's own timings for a few seconds. On Torch this is a button on the plugin's tab.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Timings() => LimiterCommands.ShowTimings();

    [Command("GetReport", "Generates a full report of this server.")]
    [Permission(MyPromoteLevel.Admin)]
    public void GetReport() => LimiterCommands.GetReport(RespondOnGameThread());

    [Command("Disable", "Disables the Advanced PB Limiter.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Disable() => LimiterCommands.SetEnabled(false);

    [Command("Enable", "Enables the Advanced PB Limiter.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Enable() => LimiterCommands.SetEnabled(true);

    [Command("AllowStaffCommands", "Allows staff to use CreatePrivilegedUser. Only the server owner can use this command.")]
    [Permission(MyPromoteLevel.Owner)]
    public string AllowStaffCommands() => LimiterCommands.SetStaffCommandsAllowed(true);

    [Command("DenyStaffCommands", "Stops staff using CreatePrivilegedUser. Only the server owner can use this command.")]
    [Permission(MyPromoteLevel.Owner)]
    public string DenyStaffCommands() => LimiterCommands.SetStaffCommandsAllowed(false);

    [Command("CreatePrivilegedUser", "Creates a new privileged user or overwrites an existing one.")]
    [Permission(MyPromoteLevel.Admin)]
    public string CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay)
        => LimiterCommands.CreatePrivilegedUser(steamId, maxRunTimeMS, maxRunTimeMSAvg, combinedLimits, maxAllBlocksCombinedRunTimeMS, maxAllBlocksCombinedRunTimeMSAvg, offencesBeforePunishment, punishment, gracefulShutDownRequestDelay);

    [Command("DetailedAdminHelp", "Gives a list of all staff commands and their usage.")]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> DetailedAdminHelp() => LimiterCommands.DetailedStaffHelp();

    [Command("AdminHelp", "Gives a list of all staff commands.")]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> AdminHelp() => LimiterCommands.StaffHelp();
}
