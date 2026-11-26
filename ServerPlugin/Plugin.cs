#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
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

    // How the save of a change made through the config went, read by IPbLimiterHost.ChangeConfig around a command's change.
    // Game thread, as the commands and Quasar's edits.
    private bool? _changeSaved;
    private Harmony? _harmony;
    private string _configPath = "";
    private bool _active;

    // Quasar finds the config through this public PluginConfig-typed property and renders its editor.
    public LimiterConfig PluginConfig => _config;

    // IPbLimiterHost
    IPbLimiterConfig IPbLimiterHost.Config => _config;
    string IPbLimiterHost.Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    // A change saves itself through OnConfigChanged: the command is answered with how that went, or saved here when its
    // change set a value the config already had, which raises no event.
    bool IPbLimiterHost.ChangeConfig(Action change)
    {
        _changeSaved = null;
        change();
        bool? changeSaved = _changeSaved;
        _changeSaved = null;
        return changeSaved ?? SaveConfig();
    }
    string? IPbLimiterHost.ConfigChangeRefusal => ManagedNodeRefusal("a command");
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
        string configPath = Path.Combine(MyFileSystem.UserDataPath, Name, Name + ".cfg");

        // A Quasar cluster runs one world across several server processes. A grid moves between them, and its programmable
        // blocks' run history, offences and punishments, kept per process, do not follow it. A cluster node also loads the
        // world before IPlugin.Init, so the session hook below would never fire. Nothing is started: no patches, commands
        // or session handlers, and no local file is read or written.
        if (PluginCluster.IsClusterProcess || ManagedPluginConfiguration.Required)
        {
            Log.Warning("Advanced PB Limiter is not supported on a Quasar cluster: a programmable block's run history and punishments do not follow its grid "
                        + "from one node to another. The plugin is inactive.");
            // Magnetar checks this plugin's PluginConfig against the cluster's canonical config, so that is loaded, in memory only.
            if (ManagedPluginConfiguration.Required)
            {
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

        Instance = this;
        _configPath = configPath;

        // The first Magnetar build kept the config in the user data root. A missing file loads as defaults with no error,
        // so without this an update would silently drop the server's limits and privileged players.
        string legacyPath = Path.Combine(MyFileSystem.UserDataPath, Name + ".cfg");

        // Before anything decides which file to read: this also restores a config an interrupted rewrite left missing.
        List<string> damage = ReplaceDamage(_configPath, out bool copyWaiting);

        // A config set aside as unreadable is still the newer one, so the old file is not read in its place, nor while a copy
        // of the newer one waits to be restored. An old file set aside while a copy of it waited (see copyWaiting below)
        // comes back from that copy in its Damage step.
        // A node of a managed cluster reads the cluster's configuration, never these files.
        bool fromLegacy = !ManagedPluginConfiguration.Required && !File.Exists(_configPath) && !copyWaiting
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
        if (!ManagedPluginConfiguration.Required && !fromLegacy && !copyWaiting && !File.Exists(_configPath))
        {
            List<string> oldSetAside = FindSetAside(legacyPath);
            if (oldSetAside.Count > 0)
                Log.Warning($"There is no config, and the old one could not be read and was set aside in {Path.GetDirectoryName(legacyPath)} as {string.Join(", ", oldSetAside)}. "
                            + $"The server runs on the defaults. Fix it and move it back to {legacyPath} to import it at the next start, or set the limits again: "
                            + "a change saves the settings as they are as the new config.");
        }

        if (fromLegacy)
        {
            damage = ReplaceDamage(legacyPath, out copyWaiting);
            // Only a copy of the old config was there, and its Damage step found it cut short and removed it: there is no
            // old config to import, so the defaults are not saved as if one had been moved.
            if (!copyWaiting && !File.Exists(legacyPath))
                fromLegacy = false;
        }
        bool loaded = false;
        string path = fromLegacy ? legacyPath : _configPath;

        try
        {
            _config = ConfigStorage.LoadXml<LimiterConfig>(path);
            loaded = true;

            // JSON is read by InstantPunishmentJsonConverter, which logs this itself as it reads.
            if (damage.Count > 0)
                Log.Warning(InstantPunishmentFile.LogLine(damage));
        }
        catch (Exception e) when (SafeFile.IsReadFailure(e))
        {
            // The file could not be opened or read, held by another program say, also when XmlSerializer wraps an I/O error
            // met part way through: nothing says it is broken, so it is left where it is, and the defaults the server runs
            // on are never saved over it.
            Log.Error($"Could not open the config {path}, using defaults; changes are not saved until the server restarts", e);
            _config = new LimiterConfig();
            _saveBlocked = "The config was not saved: the file that could not be opened is still in its place";
        }
        catch (Exception e)
        {
            Log.Error("Could not read the config, using defaults", e);
            _config = new LimiterConfig();
            SetAside(path);
        }

        // The log line from the Damage step says that copy is used at the next start, which holds only if nothing is saved
        // first, and only once the config is out of its way: set aside above. A config that could not be moved stays, and so
        // would the copy, start after start, so the admin is told to move it.
        if (copyWaiting)
        {
            _saveBlocked = $"The config was not saved: a copy from an interrupted write waits at {InstantPunishmentFile.CopyPath(path)} to become the config";
            if (File.Exists(path))
                Log.Error($"The copy at {InstantPunishmentFile.CopyPath(path)} can't become the config by itself: the config is still in its place. "
                          + $"Stop the server and move {InstantPunishmentFile.CopyPath(path)} to {path} to use the copy, or delete the copy to keep the config. Changes are not saved until then.");
        }

        // Only a config that was read is saved in the new place, so a broken old file is never replaced by defaults.
        if (fromLegacy && loaded && SaveConfig())
            Log.Info($"Moved the config from {legacyPath} to {_configPath}. The old file is no longer read and can be deleted.");

        RebuildPrivilegedLookup();
        // Edits made in Quasar arrive through the config's setters: persist them.
        _config.PropertyChanged += OnConfigChanged;

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
        RemovePatches();
        _config.PropertyChanged -= OnConfigChanged;
        Instance = null;

        if (!_active) return;
        MySession.AfterLoading -= OnSessionLoaded;
        MySession.OnUnloading -= OnSessionUnloading;
        LimiterCore.OnSessionUnloading();
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
    /// would otherwise be saved over it at the next change. An earlier file set aside is kept too. If the file can't be
    /// moved, nothing is saved over it until the server restarts. A Quasar-managed server does not read this file.
    /// </summary>
    private void SetAside(string path)
    {
        if (ManagedPluginConfiguration.Required || !File.Exists(path)) return;
        try
        {
            // A name of its own, also when files set aside earlier are in the way, see KeptFile.Keep.
            string aside = KeptFile.Keep(path, ".unreadable");
            Log.Error($"The config that could not be read was moved to {aside}. Fix it and move it back, or set the limits again.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _saveBlocked = "The config was not saved: the file that could not be read is still in its place";
            Log.Error($"Could not move the unreadable config {path} aside; changes are not saved until the server restarts", e);
        }
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
    /// Rewrites an old Damage in the config file and returns the settings changed. A Quasar-managed server reads JSON, not
    /// this file, so it is left as it is. A failure here is not the config's fault (a full disk, a file held open), so the
    /// config is then read as it is, and set aside only if that fails.
    /// </summary>
    private static List<string> ReplaceDamage(string path, out bool copyWaiting)
    {
        copyWaiting = false;
        if (ManagedPluginConfiguration.Required) return new List<string>();
        try
        {
            List<string> damage = InstantPunishmentFile.ReplaceDamage(path, out string? recovered, out copyWaiting);
            if (recovered is not null)
                Log.Warning(recovered);
            // A copy that could not even be read, held by another program say, is left for the next start: while the
            // config is missing beside it, nothing is saved either, and the log says so, as nothing else would.
            if (!copyWaiting && MissingWithCopy(path))
            {
                copyWaiting = true;
                Log.Error($"The config {path} is missing and its copy at {InstantPunishmentFile.CopyPath(path)} could not be read, held by another program say. "
                          + "The server runs on the defaults and saves nothing until the next start, which restores the copy.");
            }
            return damage;
        }
        catch (Exception e)
        {
            Log.Error("Could not check the config for an instant punishment of Damage; the config is read as it now is, the error says what that is", e);
            // A config still missing with its complete copy beside it is restored from that copy at the next start, as long
            // as nothing is saved first: the defaults saved now would be the config that start sees, and the copy only kept.
            copyWaiting = MissingWithCopy(path);
            return new List<string>();
        }
    }

    /// <summary>The config is missing and a copy beside it may still restore it: nothing may be saved until it has.</summary>
    private static bool MissingWithCopy(string path) =>
        !File.Exists(path) && SafeFile.MayRestore(InstantPunishmentFile.CopyPath(path), SafeFile.IsWholeXml);

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
