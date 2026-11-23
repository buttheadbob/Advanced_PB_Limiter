using System;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.QuasarContracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>
/// Entry points for the Quasar companion plugin. Quasar builds the companion and it cannot reference this assembly, so
/// it finds this class by name and calls it through reflection. Only strings and numbers cross over.
/// Everything except <see cref="IsOnline"/> must be called on the game thread.
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

    public static string GetReportJson() => JsonConvert.SerializeObject(ReportManager.BuildSnapshot(), Json);

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
    /// As <see cref="ApplyProfile"/>, but a server that takes no profiles, or a profile no try can apply, is answered with
    /// the reason in Refused, so Quasar stops sending it. A save that fails still throws, as a later try may work. Called
    /// by companions from 1.3.3 on, which log a failure themselves.
    /// </summary>
    public static string ApplyProfileOrRefuse(string settingsJson)
    {
        Plugin plugin = LoadedPlugin();
        if (Plugin.ProfileRefusal is { } refusal)
            return JsonConvert.SerializeObject(new ProfileData { Refused = refusal }, Json);
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

    private static Plugin LoadedPlugin() => Plugin.Instance ?? throw new InvalidOperationException("Advanced PB Limiter is not loaded on this server.");
}
