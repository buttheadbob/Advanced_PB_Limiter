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

    public static string ApplyProfile(string settingsJson)
    {
        Plugin plugin = LoadedPlugin();
        int settings = plugin.ApplyProfile(settingsJson);
        return JsonConvert.SerializeObject(new ProfileData { Settings = settings, PrivilegedPlayers = plugin.PrivilegedPlayerCount }, Json);
    }

    private static Plugin LoadedPlugin() => Plugin.Instance ?? throw new InvalidOperationException("Advanced PB Limiter is not loaded on this server.");
}
