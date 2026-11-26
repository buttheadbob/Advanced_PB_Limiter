#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
    private static readonly LimiterLog Log = new("PB Limiter Commands");

    // Reports built on another thread, and setting changes answered once saved, reply later: replies belong on the game thread.
    private Action<string> RespondOnGameThread()
    {
        CommandContext context = Context;
        return text =>
        {
            try
            {
                MySandboxGame? game = MySandboxGame.Static;
                if (game is null)
                {
                    Respond(context, text);
                    return;
                }

                game.Invoke(() => Respond(context, text), "Advanced_PB_Limiter");
            }
            catch (Exception e)
            {
                // The reply runs in the game's update loop, where an exception is not caught: a session that ended
                // while the report was building must not take the server down.
                Log.Error(e, "Could not send the PB Limiter reply");
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
            Log.Error(e, "Could not send the PB Limiter reply");
        }
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

    [Command("Runtimes", "Shows the last saved runtimes of up to 10 of the player's programmable blocks, once every 30 seconds.")]
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

    [Command("requestpbshutdown", "Sends the shutdown request to all programmable block scripts that monitor for it, with the seconds until restart, 1 if left out. Eg: !PBLimiter requestpbshutdown 10")]
    [Permission(MyPromoteLevel.Admin)]
    public string RequestPbShutdown(int seconds = 1) => LimiterCommands.RequestPbShutdown(seconds);

    [Command("debug", "Only for debugging, not regular use for servers.")]
    [Permission(MyPromoteLevel.Admin)]
    public void Debug(bool debug) => LimiterCommands.SetDebug(debug, RespondOnGameThread());

    [Command("Timings", "Logs the limiter's own timings for a few seconds. On Torch this is a button on the plugin's tab.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Timings() => LimiterCommands.ShowTimings();

    [Command("GetReport", "Generates a full report of this server.")]
    [Permission(MyPromoteLevel.Admin)]
    public void GetReport() => LimiterCommands.GetReport(RespondOnGameThread());

    [Command("Disable", "Disables the Advanced PB Limiter.")]
    [Permission(MyPromoteLevel.Admin)]
    public void Disable() => LimiterCommands.SetEnabled(false, RespondOnGameThread());

    [Command("Enable", "Enables the Advanced PB Limiter.")]
    [Permission(MyPromoteLevel.Admin)]
    public void Enable() => LimiterCommands.SetEnabled(true, RespondOnGameThread());

    [Command("AllowStaffCommands", "Allows staff to use CreatePrivilegedUser. Only the server owner can use this command.")]
    [Permission(MyPromoteLevel.Owner)]
    public void AllowStaffCommands() => LimiterCommands.SetStaffCommandsAllowed(true, RespondOnGameThread());

    [Command("DenyStaffCommands", "Stops staff using CreatePrivilegedUser. Only the server owner can use this command.")]
    [Permission(MyPromoteLevel.Owner)]
    public void DenyStaffCommands() => LimiterCommands.SetStaffCommandsAllowed(false, RespondOnGameThread());

    [Command("CreatePrivilegedUser", "Creates a new privileged user or overwrites an existing one.")]
    [Permission(MyPromoteLevel.Admin)]
    public void CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay)
        => LimiterCommands.CreatePrivilegedUser(steamId, maxRunTimeMS, maxRunTimeMSAvg, combinedLimits, maxAllBlocksCombinedRunTimeMS, maxAllBlocksCombinedRunTimeMSAvg, offencesBeforePunishment, punishment, gracefulShutDownRequestDelay, RespondOnGameThread());

    [Command("DetailedAdminHelp", "Gives a list of all staff commands and their usage.")]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> DetailedAdminHelp() =>
        LimiterCommands.DetailedStaffHelp().Append("!PBLimiter Timings - Logs the limiter's own timings for a few seconds.");

    [Command("AdminHelp", "Gives a list of all staff commands.")]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> AdminHelp() => LimiterCommands.StaffHelp().Append("!PBLimiter Timings");
}
