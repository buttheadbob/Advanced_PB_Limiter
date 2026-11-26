#nullable enable
using System;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.QuasarContracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>
/// Entry points for the Quasar companion plugin. Quasar builds the companion and it cannot reference this assembly, so
/// it finds this class by name and calls it through reflection. Only strings and numbers cross over.
/// <see cref="IsOnline"/>, <see cref="InactiveReason"/>, <see cref="GetReportJson"/> and <see cref="ReportSnapshotOnAnyThread"/> may be called from any
/// thread: they read only flags and the data the game thread cached. Everything else must be called on the game thread.
/// </summary>
// ReSharper disable UnusedMember.Global
public static class QuasarBridge
{
    private static readonly JsonSerializerSettings Json = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    /// <summary>Only reads a flag, so the companion can ask without waiting for the game thread during a world load.</summary>
    public static bool IsOnline() => LimiterCore.GameOnline;

    /// <summary>Any thread: why the limiter is inactive on this server until it restarts, or null.</summary>
    public static string? InactiveReason() => Plugin.InactiveReason;

    /// <summary>
    /// Any thread: true when this server will never apply a profile, as a Quasar cluster node, so Quasar stops sending it.
    /// False when it is inactive only until the plugin is updated: the profile is then sent again and applied after that.
    /// </summary>
    public static bool InactiveForGood() => Plugin.InactiveForGood;

    /// <summary>Any thread: the snapshot reads tracking data and the block and grid names the game thread cached.</summary>
    public static string GetReportJson() => JsonConvert.SerializeObject(ReportManager.BuildSnapshot(), Json);

    /// <summary>
    /// True when <see cref="GetReportJson"/> may be called from any thread. A companion talking to an older server plugin,
    /// which has no such method, must ask it on the game thread: there the names and working state it reads are live.
    /// </summary>
    public static bool ReportSnapshotOnAnyThread() => true;

    public static int TurnOffBlock(long entityId) => TrackingManager.TurnOffBlock(entityId);

    public static int TurnOffPlayer(long identityId) => TrackingManager.TurnOffPlayer(identityId);

    public static string GetProfileJson()
    {
        Plugin plugin = LoadedPlugin();
        return JsonConvert.SerializeObject(new ProfileData
        {
            SettingsJson = plugin.ExportProfile(),
            Settings = ProfileSettings.Count,
            PrivilegedPlayers = plugin.PrivilegedPlayerCount
        }, Json);
    }

    /// <summary>
    /// For companions before 1.3.3, whose Quasar plugin treats any answer as applied: a refusal is thrown, so it stays
    /// queued there with the reason. Those companions write nothing to the server log, so a failure is logged here.
    /// </summary>
    public static string ApplyProfile(string settingsJson)
    {
        Plugin plugin = LoadedPlugin();
        try
        {
            return Apply(plugin, settingsJson);
        }
        catch (Exception e)
        {
            plugin.LogForOldCompanion(e);
            throw;
        }
    }

    /// <summary>
    /// As <see cref="ApplyProfile"/>, but a profile no try can apply is answered with the reason in Refused, so Quasar stops
    /// sending it. A save that fails still throws, as a later try may work. Called by companions from 1.3.3 on, which log a
    /// failure themselves. A cluster node, which takes no profiles, never gets here: the companion refuses for it.
    /// </summary>
    public static string ApplyProfileOrRefuse(string settingsJson)
    {
        Plugin plugin = LoadedPlugin();
        try
        {
            return Apply(plugin, settingsJson);
        }
        catch (ProfileRejectedException e)
        {
            return JsonConvert.SerializeObject(new ProfileData { Refused = e.Message }, Json);
        }
    }

    private static string Apply(Plugin plugin, string settingsJson)
    {
        int settings = plugin.ApplyProfile(settingsJson);
        return JsonConvert.SerializeObject(new ProfileData { Settings = settings, PrivilegedPlayers = plugin.PrivilegedPlayerCount }, Json);
    }

    private static Plugin LoadedPlugin()
    {
        Plugin plugin = Plugin.Instance ?? throw new InvalidOperationException("Advanced PB Limiter is not loaded on this server.");
        if (!plugin.IsReady) throw new InvalidOperationException("Advanced PB Limiter is still loading its config; try again.");
        return plugin;
    }
}
