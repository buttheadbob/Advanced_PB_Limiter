#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.MagnetarHost.Patches;
using Advanced_PB_Limiter.QuasarContracts;
using Advanced_PB_Limiter.Utils;
using HarmonyLib;
using Newtonsoft.Json;
using PluginSdk.Clustering;
using PluginSdk.Commands;
using PluginSdk.Config;
using PluginSdk.Logging;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Plugins;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>Magnetar host: Quasar config, PluginSdk commands and Harmony patches. The limiter itself is in the shared code.</summary>
// ReSharper disable once UnusedType.Global
public sealed class Plugin : IPlugin, IPbLimiterHost
{
    public const string Name = "AdvancedPBLimiter";
    private static readonly Logger Log = Logger.Create(Name);

    internal static Plugin? Instance { get; private set; }

    // Namespaced, so no other plugin can share it and remove these patches with its own.
    private const string HarmonyId = "buttheadbob.AdvancedPBLimiter";

    private LimiterConfig _config = new();
    // Why nothing is saved until the server restarts, or null: the config could not be opened, or could not be read and moved
    // aside, or a copy from an interrupted write waits to become the config. The defaults the server runs on never replace it.
    private string? _saveBlocked;

    // How the save of a change made through the config went, read by IPbLimiterHost.ChangeConfigAsync around a command's change.
    // Game thread, as the commands and Quasar's edits.
    private bool? _changeSaved;
    private Harmony? _harmony;
    private string _configPath = "";
    private bool _active;

    // The config file is read beside the world load, on a pool thread, and its settings copied into the live config instance
    // when read. Until then nothing is enforced and no setting changes: the session-loaded step, the limiter's Update,
    // commands that change settings and profiles all wait for it. The session-loaded step itself runs only on the game
    // thread, from the session event or the first Update after the config is ready, so it runs exactly once per session.
    private readonly object _stateLock = new();
    private int _ready;
    private bool _disposed;
    private bool _sessionLoaded;
    private bool _sessionLoadRaised;

    internal bool IsReady => Volatile.Read(ref _ready) != 0;

    /// <summary>Why the limiter is inactive until the server restarts, or null: a Quasar cluster node, or a game it does not match.</summary>
    internal static string? InactiveReason { get; private set; }

    // Quasar finds the config through this public PluginConfig-typed property and renders its editor.
    public LimiterConfig PluginConfig => _config;

    // IPbLimiterHost
    IPbLimiterConfig IPbLimiterHost.Config => _config;
    string IPbLimiterHost.Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    // A change saves itself through OnConfigChanged: the command is answered with how that went, or saved here when its
    // change set a value the config already had, which raises no event. PluginSdk's ConfigStorage writes the file
    // synchronously, so the save is done when this returns.
    Task<bool> IPbLimiterHost.ChangeConfigAsync(Action change)
    {
        if (!IsReady)
        {
            // The commands ask ConfigChangeRefusal first, which refuses while the config loads.
            Log.Warning("A setting change came before the config had loaded; it was not applied");
            return Task.FromResult(false);
        }
        _changeSaved = null;
        change();
        bool? changeSaved = _changeSaved;
        _changeSaved = null;
        return Task.FromResult(changeSaved ?? SaveConfig());
    }
    string? IPbLimiterHost.ConfigChangeRefusal => IsReady ? ManagedNodeRefusal("a command") : StillLoading;
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

    internal const string StillLoading = "Advanced PB Limiter is still loading its config; nothing was changed. Try again in a moment.";

    public void Init(object gameInstance)
    {
        Log.Info("Loading");
        InactiveReason = null;

        string configPath = Path.Combine(MyFileSystem.UserDataPath, Name, Name + ".cfg");
        // The first Magnetar build kept the config in the user data root. A missing file loads as defaults with no error,
        // so without this an update would silently drop the server's limits and privileged players.
        string legacyPath = Path.Combine(MyFileSystem.UserDataPath, Name + ".cfg");

        // A Quasar cluster runs one world across several server processes. A grid moves between them, and its programmable
        // blocks' run history, offences and punishments, kept per process, do not follow it. A cluster node also loads the
        // world before IPlugin.Init, so the session hook below would never fire. Nothing is started: no patches, commands
        // or session handlers, and no local file is read or written.
        if (PluginCluster.IsClusterProcess || ManagedPluginConfiguration.Required)
        {
            InactiveReason = "Advanced PB Limiter is not supported on a Quasar cluster: a programmable block's run history and punishments do not follow its grid from one node to another. It is inactive on this node.";
            Log.Warning(InactiveReason);
            if (ManagedPluginConfiguration.Required)
            {
                // Magnetar checks this plugin's PluginConfig against the cluster's canonical config as soon as Init returns,
                // so that is loaded, in memory only.
                try
                {
                    _config = ConfigStorage.LoadXml<LimiterConfig>(configPath);
                }
                catch (Exception e)
                {
                    Log.Error("Could not read the cluster's canonical config", e);
                }
            }
            return;
        }

        _configPath = configPath;
        Instance = this;

        try
        {
            LimiterCore.Init(this);
            _harmony = new Harmony(HarmonyId);
            // Each patch class by name, so a patch that no longer applies fails here, and nothing else in the assembly is patched.
            foreach (Type patch in new[] { typeof(ExecuteCodePatch), typeof(RunPatch), typeof(CompilePatch), typeof(UpdateStoragePatch) })
                _harmony.CreateClassProcessor(patch).Patch();
        }
        catch (Exception e)
        {
            // A game update that changed something the limiter needs must not take the server down, the plugin just stays
            // inactive. The patches that did apply are removed again, or they would keep collecting runs nothing reads.
            RemovePatches();
            // Nothing is loaded as far as commands and the Quasar companion are concerned: without this they would find
            // the half-started plugin and write settings nothing enforces until a restart.
            Instance = null;
            InactiveReason = "The game no longer matches this version of Advanced PB Limiter: it is inactive until the plugin is updated.";
            Log.Critical(InactiveReason, e);
            // The config is still read, so Quasar's editor shows the server's settings and saves its edits.
            _ = ReadConfigAsync(legacyPath);
            return;
        }

        // The same two events Torch reports as its Loaded and Unloading session states.
        MySession.AfterLoading += OnSessionLoaded;
        MySession.OnUnloading += OnSessionUnloading;
        _active = true;

        // On the loader thread, as Magnetar runs every plugin's Init: its command registry has no lock. A command that
        // changes a setting is refused until the config has loaded, see ConfigChangeRefusal.
        try
        {
            ServerCommands.Register(Assembly.GetExecutingAssembly());
        }
        catch (Exception e)
        {
            Log.Error("The commands could not be registered; the limiter itself keeps running", e);
        }

        _ = ReadConfigAsync(legacyPath);
        Log.Info("Loaded (Magnetar)");
    }

    /// <summary>Game thread. Runs the limiter once the config is ready, starting it on the loaded world first.</summary>
    public void Update()
    {
        if (!_active || !IsReady) return;
        if (_sessionLoaded && !_sessionLoadRaised)
        {
            _sessionLoadRaised = true;
            RaiseSessionLoaded();
        }
        LimiterCore.Update();
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _config.PropertyChanged -= OnConfigChanged;
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        RemovePatches();

        if (!_active) return;
        _active = false;
        MySession.AfterLoading -= OnSessionLoaded;
        MySession.OnUnloading -= OnSessionUnloading;
        LimiterCore.OnSessionUnloading();
    }

    /// <summary>What <see cref="ReadConfigAsync"/> read, for <see cref="ApplyStart"/>.</summary>
    private sealed class ConfigStart
    {
        public ConfigStart(string path, LimiterConfig loaded, bool loadedOk, List<string> damage, bool copyWaiting, bool fromLegacy, string? saveBlocked)
        {
            Path = path;
            Loaded = loaded;
            LoadedOk = loadedOk;
            Damage = damage;
            CopyWaiting = copyWaiting;
            FromLegacy = fromLegacy;
            SaveBlocked = saveBlocked;
        }

        public string Path { get; }
        public LimiterConfig Loaded { get; }
        public bool LoadedOk { get; }
        public List<string> Damage { get; }
        public bool CopyWaiting { get; }
        public bool FromLegacy { get; }
        public string? SaveBlocked { get; }
    }

    /// <summary>
    /// Pool thread: deals with the config file, reads it and applies it. A failure leaves the defaults, with saves blocked so
    /// they never replace the file.
    /// </summary>
    private async Task ReadConfigAsync(string legacyPath)
    {
        ConfigStart start;
        try
        {
            (List<string> damage, bool copyWaiting) = await ReplaceDamageAsync(_configPath).ConfigureAwait(false);

            // A config set aside as unreadable is still the newer one, so the old file is not read in its place, nor while a copy
            // of the newer one waits to be restored. An old file set aside while a copy of it waited (see copyWaiting below)
            // comes back from that copy in its Damage step.
            bool fromLegacy = !File.Exists(_configPath) && !copyWaiting
                              && (File.Exists(legacyPath) || File.Exists(InstantPunishmentFile.CopyPath(legacyPath)));
            List<string> setAside = fromLegacy ? FindSetAside(_configPath) : new List<string>();
            if (setAside.Count > 0)
            {
                fromLegacy = false;
                Log.Warning($"Not importing the old config {legacyPath}: the newer config could not be read and was set aside in {Path.GetDirectoryName(_configPath)} as {string.Join(", ", setAside)}. "
                            + $"Fix it and move it back to {_configPath}. To import the old config instead, remove every {Path.GetFileName(_configPath)}*.unreadable file and restart "
                            + "before any setting is changed: a change saves the defaults as the new config, which is then read from then on.");
            }
            // An old config set aside as unreadable at an earlier start, with no config since: said at every start until one is
            // saved, as the server runs on the defaults meanwhile and nothing else would say so.
            if (!fromLegacy && !copyWaiting && !File.Exists(_configPath))
            {
                List<string> oldSetAside = FindSetAside(legacyPath);
                if (oldSetAside.Count > 0)
                    Log.Warning($"There is no config, and the old one could not be read and was set aside in {Path.GetDirectoryName(legacyPath)} as {string.Join(", ", oldSetAside)}. "
                                + $"The server runs on the defaults. Fix it and move it back to {legacyPath} to import it at the next start, or set the limits again: "
                                + "a change saves the settings as they are as the new config.");
            }

            if (fromLegacy)
            {
                (damage, copyWaiting) = await ReplaceDamageAsync(legacyPath).ConfigureAwait(false);
                // Only a copy of the old config was there, and its Damage step found it cut short and removed it: there is no
                // old config to import, so the defaults are not saved as if one had been moved.
                if (!copyWaiting && !File.Exists(legacyPath))
                    fromLegacy = false;
            }
            string path = fromLegacy ? legacyPath : _configPath;

            LimiterConfig loaded = new();
            bool loadedOk = false;
            string? saveBlocked = null;
            try
            {
                loaded = ConfigStorage.LoadXml<LimiterConfig>(path);
                loadedOk = true;
            }
            catch (Exception e) when (SafeFile.IsReadFailure(e))
            {
                // The file could not be opened or read, held by another program say, also when XmlSerializer wraps an I/O error
                // met part way through: nothing says it is broken, so it is left where it is, and the defaults the server runs
                // on are never saved over it.
                Log.Error($"Could not open the config {path}, using defaults; changes are not saved until the server restarts", e);
                saveBlocked = "The config was not saved: the file that could not be opened is still in its place";
            }
            catch (Exception e)
            {
                Log.Error("Could not read the config, using defaults", e);
                saveBlocked = SetAside(path);
            }

            start = new ConfigStart(path, loaded, loadedOk, damage, copyWaiting, fromLegacy, saveBlocked);
        }
        catch (Exception e)
        {
            Log.Error("Could not finish loading the config; the limiter runs on the defaults and saves no change until the server restarts", e);
            start = new ConfigStart(_configPath, new LimiterConfig(), false, new List<string>(), false, false,
                                    "The config was not saved: it could not be loaded, so the settings in use are the defaults");
        }
        ApplyStart(start);
    }

    /// <summary>
    /// Copies the config that was read into the live instance, which the limiter, patches and Magnetar's editor hold, and
    /// lets the limiter run. Under the state lock, so a Dispose meanwhile either comes first and nothing is applied, or after.
    /// </summary>
    private void ApplyStart(ConfigStart start)
    {
        // The log line from the Damage step says that copy is used at the next start, which holds only if nothing is saved
        // first, and only once the config is out of its way: set aside above. A config that could not be moved stays, and so
        // would the copy, start after start, so the admin is told to move it. Blocked before any change can arrive.
        string copy = InstantPunishmentFile.CopyPath(start.Path);
        string? saveBlocked = start.CopyWaiting
            ? $"The config was not saved: a copy from an interrupted write waits at {copy} to become the config"
            : start.SaveBlocked;
        lock (_stateLock)
        {
            if (_disposed) return;
            if (saveBlocked is not null) _saveBlocked = saveBlocked;
            CopyInto(_config, start.Loaded);
            _config.PropertyChanged += OnConfigChanged;
        }
        if (start.CopyWaiting && File.Exists(start.Path))
            Log.Error($"The copy at {copy} can't become the config by itself: the config is still in its place. "
                      + $"Stop the server and move {copy} to {start.Path} to use the copy, or delete the copy to keep the config. Changes are not saved until then.");

        // JSON is read by InstantPunishmentJsonConverter, which logs this itself as it reads.
        if (start.Damage.Count > 0 && start.LoadedOk)
            Log.Warning(InstantPunishmentFile.LogLine(start.Damage));

        // Only a config that was read is saved in the new place, so a broken old file is never replaced by defaults.
        if (start.FromLegacy && start.LoadedOk && SaveConfig())
            Log.Info($"Moved the config from {start.Path} to {_configPath}. The old file is no longer read and can be deleted.");

        RebuildPrivilegedLookup();
        Volatile.Write(ref _ready, 1);
    }

    /// <summary>Copies every setting into the live config instance, which patches and Magnetar's editor hold.</summary>
    private static void CopyInto(LimiterConfig target, LimiterConfig source)
    {
        foreach (PropertyInfo property in ConfigProperties)
            property.SetValue(target, property.GetValue(source));
    }

    /// <summary>Only this plugin's own patches: UnpatchAll() would remove other plugins' and the loader's as well.</summary>
    private void RemovePatches()
    {
        if (_harmony is null) return;
        foreach (MethodBase original in new List<MethodBase>(_harmony.GetPatchedMethods()))
            _harmony.Unpatch(original, HarmonyPatchType.All, _harmony.Id);
        _harmony = null;
    }

    /// <summary>
    /// Keeps a config file that could not be read for the admin to look at and fix: the defaults the server now runs on
    /// would otherwise be saved over it at the next change. An earlier file set aside is kept too. Returns why nothing may
    /// be saved when the file can't be moved, or null. A Quasar-managed server does not read this file.
    /// </summary>
    private static string? SetAside(string path)
    {
        if (ManagedPluginConfiguration.Required || !File.Exists(path)) return null;
        try
        {
            // A name of its own, also when files set aside earlier are in the way, see KeptFile.Keep.
            string aside = KeptFile.Keep(path, ".unreadable");
            Log.Error($"The config that could not be read was moved to {aside}. Fix it and move it back, or set the limits again.");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not move the unreadable config {path} aside; changes are not saved until the server restarts", e);
            return "The config was not saved: the file that could not be read is still in its place";
        }
    }

    /// <summary>Game thread. The world loaded: the limiter starts on it now, or at the first Update once the config is ready.</summary>
    private void OnSessionLoaded()
    {
        if (_disposed || _sessionLoaded) return;
        _sessionLoaded = true;
        if (!IsReady || _sessionLoadRaised) return;
        _sessionLoadRaised = true;
        RaiseSessionLoaded();
    }

    private void OnSessionUnloading()
    {
        if (_disposed) return;
        _sessionLoaded = false;
        _sessionLoadRaised = false;
        Log.Info("Session Unloading!");
        LimiterCore.OnSessionUnloading();
    }

    private static void RaiseSessionLoaded()
    {
        Log.Info("Session Loaded!");
        LimiterCore.OnSessionLoaded();
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
            _changeSaved = SaveConfig();
    }

    internal string ExportProfile() => ProfileSettings.Export(_config);

    internal int PrivilegedPlayerCount => _config.PrivilegedPlayers.Count;

    /// <summary>
    /// Replaces the settings from a Quasar profile, on the game thread. All or nothing: the profile is first saved from a
    /// copy of the config, and only once that worked does the live config change, all at once with its change handler off,
    /// so the file is saved once and limits and privileged players change in the same tick. When the file can't be saved
    /// nothing the server or anyone watching the config sees has changed, and this throws, so Quasar never shows a profile
    /// as applied that the next restart would lose, and keeps it queued to send again.
    /// </summary>
    internal int ApplyProfile(string settingsJson)
    {
        if (Volatile.Read(ref _ready) == 0)
            throw new InvalidOperationException("Advanced PB Limiter is still loading its config; the profile is sent again.");
        if (ProfileRefusal is not null)
            throw new InvalidOperationException(ProfileRefusal);

        List<KeyValuePair<PropertyInfo, object>> values;
        List<string> damage;
        try
        {
            values = ProfileSettings.Read(settingsJson, out damage);
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException or ArgumentException or FormatException or OverflowException)
        {
            // The same profile fails the same way every time it is sent: refused for good, not retried.
            throw new ProfileRejectedException("This server can't take the profile: " + e.Message, e);
        }

        LimiterConfig candidate = CopyOf(_config);
        foreach (KeyValuePair<PropertyInfo, object> value in values)
            value.Key.SetValue(candidate, value.Value);
        if (!TrySaveConfig(candidate, out string? failure, out Exception? error))
        {
            // Not logged here: the companion writes this to the server log with its cause, once per reason, as Quasar
            // sends the profile again every few seconds while the file can't be saved; for a companion before 1.3.3,
            // which logs nothing, QuasarBridge.ApplyProfile does through LogForOldCompanion.
            throw new InvalidOperationException("The profile was not applied, the settings stay as they were: the server could not save its config (" + failure!.TrimEnd('.') + "). Quasar sends it again.", error);
        }

        SetQuietly(values.Select(value => new KeyValuePair<PropertyInfo, object?>(value.Key, value.Value)));
        // The rows left out are logged only now the profile is applied, as the Damage warning below.
        List<string> unusable = _config.RebuildPrivilegedLookup();

        // Only once applied: a profile Quasar sends again while the file can't be saved would repeat it on each try.
        if (damage.Count > 0)
            Log.Warning("The profile " + InstantPunishmentFile.LogLine(damage).Substring("The config ".Length));
        LogLeftOut(unusable);
        Log.Info($"Applied a Quasar profile: {InvariantText.Count(values.Count, "setting", "settings")}, {InvariantText.Count(_config.PrivilegedPlayers.Count, "privileged player", "privileged players")}");
        return values.Count;
    }

    // The newest failures logged for a companion before 1.3.3, by message, with when. Game thread, as ApplyProfile.
    private readonly RecentReasons _oldCompanionLogged = new();

    /// <summary>
    /// Logs why a profile from a companion before 1.3.3 was not applied, as that companion writes nothing to the server log:
    /// once per reason, and again after 10 minutes, as the companions from 1.3.3 on do. Game thread.
    /// </summary>
    internal void LogForOldCompanion(Exception error)
    {
        if (_oldCompanionLogged.IsNew(ReportOperations.ProfileApply, error.Message))
            Log.Error(error.Message, error.InnerException ?? error);
    }

    /// <summary>
    /// Logs the settings whose config JSON held Damage, or 1, read as TurnOff once the config was read whole: a config
    /// Quasar applies at runtime. On a node of a managed cluster the JSON is the cluster's canonical config, which PluginSdk
    /// then refuses as a whole, as it no longer saves back to the same text; its own message names no setting, so this
    /// line does.
    /// </summary>
    internal static void LogJsonDamage(List<string> settings)
    {
        if (ManagedPluginConfiguration.Required)
            Log.Warning($"The cluster's plugin configuration holds Damage for {string.Join(" and ", settings)}, which an instant kill no longer offers. "
                        + "Read as TurnOff it no longer matches the cluster's configuration, so this node can't use it: set TurnOff or Destroy for the cluster in Quasar.");
        else
            Log.Warning(InstantPunishmentFile.LogLine(settings));
    }

    /// <summary>
    /// Why this server takes no profiles, or null. A node of a managed cluster runs the cluster's canonical configuration,
    /// which Quasar activates for the whole cluster and refuses to change on one node; PluginSdk refuses any save that
    /// differs from it. Such a refusal is final, so Quasar stops sending the profile.
    /// </summary>
    internal static string? ProfileRefusal => ManagedNodeRefusal("a profile");

    /// <summary>Why <paramref name="what"/> may not change this server's settings, or null when it may: see ProfileRefusal.</summary>
    private static string? ManagedNodeRefusal(string what) => ManagedPluginConfiguration.Required
        ? "This server is a node of a managed cluster: its settings come from the cluster's plugin configuration, which Quasar activates for the whole cluster. Change them there; " + what + " can't change one node."
        : null;

    // Every setting of the config, read once: what a copy for a profile's trial save takes over.
    private static readonly PropertyInfo[] ConfigProperties = typeof(LimiterConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
        .ToArray();

    /// <summary>
    /// A config with the same settings, nobody watching it: a profile is saved from it before the live config changes. Lists
    /// are shared, not copied, as a profile replaces a list whole and a save only reads it.
    /// </summary>
    private static LimiterConfig CopyOf(LimiterConfig config)
    {
        LimiterConfig copy = new();
        foreach (PropertyInfo property in ConfigProperties)
            property.SetValue(copy, property.GetValue(config));
        return copy;
    }

    /// <summary>Sets the values with the config's change handler off, so nothing is saved per setting.</summary>
    private void SetQuietly(IEnumerable<KeyValuePair<PropertyInfo, object?>> values)
    {
        _config.PropertyChanged -= OnConfigChanged;
        try
        {
            foreach (KeyValuePair<PropertyInfo, object?> value in values)
                value.Key.SetValue(_config, value.Value);
        }
        finally
        {
            _config.PropertyChanged += OnConfigChanged;
        }
    }

    private void RebuildPrivilegedLookup() => LogLeftOut(_config.RebuildPrivilegedLookup());

    private static void LogLeftOut(List<string> names)
    {
        foreach (string name in names)
            Log.Warning($"The privileged player \"{name}\" is left out, its Steam ID is not a number");
    }

    /// <summary>False when it was not saved, with the reason logged.</summary>
    private bool SaveConfig()
    {
        if (TrySaveConfig(out string? failure, out Exception? error)) return true;
        if (error is null)
            Log.Warning(failure!);
        else
            Log.Error(failure!, error);
        return false;
    }

    /// <summary>Saves the config without logging; <paramref name="failure"/> says why it was not saved.</summary>
    private bool TrySaveConfig(out string? failure, out Exception? error) => TrySaveConfig(_config, out failure, out error);

    /// <summary>As <see cref="TrySaveConfig(out string?, out Exception?)"/>, writing <paramref name="config"/>: a copy, for a profile.</summary>
    private bool TrySaveConfig(LimiterConfig config, out string? failure, out Exception? error)
    {
        error = null;
        if (_saveBlocked is not null)
        {
            failure = _saveBlocked;
            return false;
        }

        try
        {
            ConfigStorage.SaveXml(config, _configPath);
            failure = null;
            return true;
        }
        catch (Exception e)
        {
            failure = "Could not save the config: " + e.Message;
            error = e;
            return false;
        }
    }

    /// <summary>
    /// Rewrites an old Damage in the config file and returns the settings changed. A failure here is not the config's
    /// fault (a full disk, a file held open), so the config is then read as it is, and set aside only if that fails.
    /// </summary>
    private static async Task<(List<string> Damage, bool CopyWaiting)> ReplaceDamageAsync(string path)
    {
        bool copyWaiting = false;
        if (ManagedPluginConfiguration.Required) return (new List<string>(), false);
        try
        {
            InstantPunishmentFile.ReplaceDamageResult result = await InstantPunishmentFile.ReplaceDamageAsync(path, moveConfigAside: false).ConfigureAwait(false);
            copyWaiting = result.CopyWaiting;
            if (result.Recovered is not null)
                Log.Warning(result.Recovered);
            // A copy that could not even be read, held by another program say, is left for the next start: while the
            // config is missing beside it, nothing is saved either, and the log says so, as nothing else would.
            if (!copyWaiting && await MissingWithCopyAsync(path).ConfigureAwait(false))
            {
                copyWaiting = true;
                Log.Error($"The config {path} is missing and its copy at {InstantPunishmentFile.CopyPath(path)} could not be read, held by another program say. "
                          + "The server runs on the defaults and saves nothing until the next start, which restores the copy.");
            }
            return (result.Changed, copyWaiting);
        }
        catch (Exception e)
        {
            Log.Error("Could not check the config for an instant punishment of Damage; the config is read as it now is, the error says what that is", e);
            // A config still missing with its complete copy beside it is restored from that copy at the next start, as long
            // as nothing is saved first: the defaults saved now would be the config that start sees, and the copy only kept.
            copyWaiting = await MissingWithCopyAsync(path).ConfigureAwait(false);
            return (new List<string>(), copyWaiting);
        }
    }

    /// <summary>The config is missing and a copy beside it may still restore it: nothing may be saved until it has.</summary>
    private static async Task<bool> MissingWithCopyAsync(string path) =>
        !File.Exists(path) && await SafeFile.MayRestoreAsync(InstantPunishmentFile.CopyPath(path), SafeFile.IsWholeXml).ConfigureAwait(false);

    /// <summary>
    /// The configs set aside as unreadable, by file name: every &lt;name&gt;*.unreadable beside it, which is what the warning
    /// tells the admin to remove to import the old config instead. Empty when there are none. Rename keeps a file's times,
    /// so nothing tells reliably which was set aside last; the warning names them all.
    /// </summary>
    private static List<string> FindSetAside(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder)) return new List<string>();
        List<string> names = new();
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, Path.GetFileName(path) + "*.unreadable"))
                names.Add(Path.GetFileName(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only a warning depends on it, and it runs before the limiter starts: it must not stop the plugin loading.
            Log.Warning($"Could not look for configs set aside in {folder}: {e.Message}");
            return new List<string>();
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
