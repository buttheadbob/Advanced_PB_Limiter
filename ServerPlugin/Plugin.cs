using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using HarmonyLib;
using PluginSdk.Commands;
using PluginSdk.Config;
using PluginSdk.Logging;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Plugins;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>Magnetar host: Quasar config, PluginSdk commands and Harmony patches. The limiter itself is in the shared code.</summary>
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, IPbLimiterHost
{
    public const string Name = "AdvancedPBLimiter";
    private static readonly Logger Log = Logger.Create(Name);

    internal static Plugin? Instance { get; private set; }

    private LimiterConfig _config = new();
    private string _configPath = "";
    private bool _active;

    // Quasar finds the config through this public PluginConfig-typed property and renders its editor.
    public LimiterConfig PluginConfig => _config;

    // IPbLimiterHost
    IPbLimiterConfig IPbLimiterHost.Config => _config;
    string IPbLimiterHost.Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    void IPbLimiterHost.SaveConfig() => SaveConfig();
    void IPbLimiterHost.LogTrace(string message) => Log.Debug(message);
    void IPbLimiterHost.LogInfo(string message) => Log.Info(message);
    void IPbLimiterHost.LogWarn(string message) => Log.Warning(message);
    void IPbLimiterHost.LogError(string message, Exception? exception)
    {
        if (exception is null)
            Log.Error(message);
        else
            Log.Error(message, exception);
    }

    public void Init(object gameInstance)
    {
        Log.Info("Loading");
        Instance = this;

        _configPath = Path.Combine(MyFileSystem.UserDataPath, Name, Name + ".cfg");

        // The first Magnetar build kept the config in the user data root. A missing file loads as defaults with no error,
        // so without this an update would silently drop the server's limits and privileged players.
        string legacyPath = Path.Combine(MyFileSystem.UserDataPath, Name + ".cfg");
        bool fromLegacy = !File.Exists(_configPath) && File.Exists(legacyPath);
        bool loaded = false;
        try
        {
            _config = ConfigStorage.LoadXml<LimiterConfig>(fromLegacy ? legacyPath : _configPath);
            loaded = true;
        }
        catch (Exception e)
        {
            Log.Error("Could not read the config, using defaults", e);
            _config = new LimiterConfig();
        }

        // Only a config that was read is saved in the new place, so a broken old file is never replaced by defaults.
        if (fromLegacy && loaded)
        {
            SaveConfig();
            Log.Info($"Moved the config from {legacyPath} to {_configPath}. The old file is no longer read and can be deleted.");
        }

        RebuildPrivilegedLookup();
        // Edits made in Quasar arrive through the config's setters: persist them.
        _config.PropertyChanged += OnConfigChanged;

        try
        {
            LimiterCore.Init(this);
            new Harmony(Name).PatchAll(Assembly.GetExecutingAssembly());
        }
        catch (Exception e)
        {
            // A game update that changed something the limiter needs must not take the server down, the plugin just stays inactive.
            Log.Critical("The game no longer matches this version, Advanced PB Limiter is inactive", e);
            return;
        }

        // The same two events Torch reports as its Loaded and Unloading session states.
        MySession.AfterLoading += OnSessionLoaded;
        MySession.OnUnloading += OnSessionUnloading;
        _active = true;

        ServerCommands.Register(Assembly.GetExecutingAssembly());
        Log.Info("Loaded (Magnetar)");
    }

    public void Update() => LimiterCore.Update();

    public void Dispose()
    {
        // IMPORTANT: Do NOT call harmony.UnpatchAll() here, it may break other plugins.
        _config.PropertyChanged -= OnConfigChanged;
        Instance = null;

        if (!_active) return;
        MySession.AfterLoading -= OnSessionLoaded;
        MySession.OnUnloading -= OnSessionUnloading;
        LimiterCore.OnSessionUnloading();
    }

    private static void OnSessionLoaded()
    {
        Log.Info("Session Loaded!");
        LimiterCore.OnSessionLoaded();
    }

    private static void OnSessionUnloading()
    {
        Log.Info("Session Unloading!");
        LimiterCore.OnSessionUnloading();
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A privileged row with 0 in these uses the server's value, so they count as well.
        if (e.PropertyName is nameof(LimiterConfig.PrivilegedPlayers) or nameof(LimiterConfig.MaxOffencesBeforePunishment)
            or nameof(LimiterConfig.GracefulShutDownRequestDelay) or nameof(LimiterConfig.PunishDamageAmount))
        {
            RebuildPrivilegedLookup();
        }

        // Runtime only, see LimiterConfig.ShowTimingsBurp.
        if (e.PropertyName != nameof(LimiterConfig.ShowTimingsBurp))
            SaveConfig();
    }

    internal string ExportProfile() => ProfileSettings.Export(_config);

    internal int PrivilegedPlayerCount => _config.PrivilegedPlayers.Count;

    /// <summary>
    /// Replaces the settings from a Quasar profile, on the game thread. The handler is off for this so the file is
    /// saved once, not once per setting, and limits and privileged players change in the same tick.
    /// </summary>
    internal int ApplyProfile(string settingsJson)
    {
        List<KeyValuePair<PropertyInfo, object>> values = ProfileSettings.Read(settingsJson);

        _config.PropertyChanged -= OnConfigChanged;
        try
        {
            foreach (KeyValuePair<PropertyInfo, object> value in values)
                value.Key.SetValue(_config, value.Value);
        }
        finally
        {
            _config.PropertyChanged += OnConfigChanged;
        }

        RebuildPrivilegedLookup();
        SaveConfig();
        Log.Info($"Applied a Quasar profile: {values.Count} settings, {_config.PrivilegedPlayers.Count} privileged players");
        return values.Count;
    }

    private void RebuildPrivilegedLookup()
    {
        foreach (string name in _config.RebuildPrivilegedLookup())
            Log.Warning($"The privileged player \"{name}\" is left out, its Steam ID is not a number");
    }

    private void SaveConfig()
    {
        try
        {
            ConfigStorage.SaveXml(_config, _configPath);
        }
        catch (Exception e)
        {
            Log.Error("Could not save the config", e);
        }
    }
}
