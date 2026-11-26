using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Xml;
using System.Xml.Linq;
using Advanced_PB_Limiter.Patches;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Session;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.UI;
using Advanced_PB_Limiter.Utils;
using Torch.Managers;
using Torch.Managers.PatchManager;

namespace Advanced_PB_Limiter
{
    /// <summary>Torch host: WPF config and Torch commands. The limiter itself is in the shared code.</summary>
    public sealed class AdvancedPBLimiterPlugin : TorchPluginBase, IWpfPlugin, IPbLimiterHost
    {
        internal static readonly TorchLog Log = new("Advanced PB Limiter");
        private const string CONFIG_FILE_NAME = "Advanced_PB_LimiterConfig.cfg";
        public static AdvancedPBLimiterPlugin? Instance { get; private set; }
        private Advanced_PB_LimiterControl? _control;
        public UserControl GetControl() => _control ??= new();
        private ConfigFile? _config;
        private TorchSessionManager? _sessionManager;
        public Advanced_PB_LimiterConfig? Config => _config?.Data;

        // The config file is read beside Torch's start, on a pool thread, and its settings copied into the live config
        // instance, which the tab and the limiter hold from Init, when read. Until then nothing is enforced and nothing is
        // saved: the session-loaded step, the limiter's Update and commands that change settings wait for it. The
        // session-loaded step itself runs only on the game thread, from the session event or the first Update after the
        // config is ready, so it runs exactly once per session.
        private readonly object _stateLock = new();
        private int _ready;
        private bool _disposed;
        private bool _sessionLoaded;
        private bool _sessionLoadRaised;

        internal bool IsReady => Volatile.Read(ref _ready) != 0;

        private const string StillLoading = "Advanced PB Limiter is still loading its config; nothing was changed. Try again in a moment.";

        // IPbLimiterHost
        IPbLimiterConfig IPbLimiterHost.Config => Config!;
        // The change arms the autosave; the save here writes it now and calls that autosave off, so the file is written once.
        async Task<bool> IPbLimiterHost.ChangeConfigAsync(Action change)
        {
            if (!IsReady)
            {
                // The commands ask ConfigChangeRefusal first, which refuses while the config loads.
                Log.Warn("A setting change came before the config had loaded; it was not applied");
                return false;
            }
            change();
            return await SaveAsync().ConfigureAwait(false);
        }
        // Torch has no managed configuration: every command may change the config once it has loaded.
        string? IPbLimiterHost.ConfigChangeRefusal => IsReady ? null : StillLoading;
        void IPbLimiterHost.LogTrace(string message) => Log.Trace(message);
        void IPbLimiterHost.LogInfo(string message) => Log.Info(message);
        void IPbLimiterHost.LogWarn(string message) => Log.Warn(message);
        void IPbLimiterHost.LogError(string message, Exception? exception)
        {
            if (exception is null)
                Log.Error(message);
            else
                Log.Error(exception, message);
        }

        // Set by the first Init. Torch's ReloadPlugins calls Init again on a plugin it does not reload, without Dispose first.
        private bool _initialized;

        /// <summary>
        /// Runs once per instance. A second call, from PluginManager.ReloadPlugins for a plugin it keeps, would add a second
        /// set of patches and a second session handler, and every offence would then count twice; it returns at once.
        /// </summary>
        public override void Init(ITorchBase torch)
        {
            if (_initialized)
            {
                Log.Info("Torch started the plugin again without stopping it first; it keeps running as it was.");
                return;
            }
            _initialized = true;
            base.Init(torch);

            string configFile = Path.Combine(StoragePath, CONFIG_FILE_NAME);
            _config = ConfigFile.Defaults(configFile);
            Instance = this; // Before anything that can fail, the plugin's tab needs it either way.

            try
            {
                LimiterCore.Init(this);
                PatchManager patchManager = torch.Managers.GetManager<PatchManager>() ?? throw new InvalidOperationException("Torch has no patch manager.");
                ProfilerPatch_Torch.Patch(patchManager);
            }
            catch (Exception e)
            {
                // A game update that changed something the limiter needs must not take the server down.
                Log.Fatal(e, "The game no longer matches this version, Advanced PB Limiter is inactive");
                LimiterCore.RetireMismatched();
                // The config is still read, so the tab shows the server's settings rather than the defaults.
                _ = InitializeAsync(configFile);
                return;
            }

            _sessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (_sessionManager != null)
                _sessionManager.SessionStateChanged += SessionChanged;
            else
                Log.Warn("No session manager loaded!");

            _ = InitializeAsync(configFile);
        }

        /// <summary>
        /// Pool thread: deals with the config file, reads it and copies it into the live instance. A failure leaves the
        /// defaults, with saves refused so they never replace the file.
        /// </summary>
        private async Task InitializeAsync(string configFile)
        {
            try
            {
                // An interrupted save is put right first, so the Damage check reads the config the save left.
                await ConfigFile.RecoverInterruptedSaveAsync(configFile).ConfigureAwait(false);

                try
                {
                    InstantPunishmentFile.ReplaceDamageResult result = await InstantPunishmentFile.ReplaceDamageAsync(configFile).ConfigureAwait(false);
                    if (result.Recovered is not null)
                        Log.Warn(result.Recovered);
                    if (result.Changed.Count > 0)
                        Log.Warn(InstantPunishmentFile.LogLine(result.Changed));
                }
                catch (Exception e)
                {
                    Log.Warn(e, "Could not check the config for an instant punishment of Damage");
                }

                ConfigFile loaded = await ConfigFile.LoadAsync(configFile).ConfigureAwait(false);
                if (loaded.IsNew)
                    Log.Info("Using the default config.");
                else if (!LimiterCore.Retired)
                    await KeepOlderDefaultsAsync(configFile, loaded).ConfigureAwait(false);

                lock (_stateLock)
                {
                    if (!_disposed)
                    {
                        _config!.Adopt(loaded);
                        Volatile.Write(ref _ready, 1);
                    }
                }
                // Adopt took over a change it still had waiting, so this saves nothing.
                loaded.Dispose();
                Log.Info("Loaded (Torch)");
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not finish loading the config; the limiter runs on the defaults and saves no change until Torch restarts");
                lock (_stateLock)
                {
                    if (_disposed) return;
                    _config!.RefuseSaves("the config could not be loaded");
                    Volatile.Write(ref _ready, 1);
                }
            }
        }

        /// <summary>Game thread. Runs the limiter once the config is ready, starting it on the loaded world first.</summary>
        public override void Update()
        {
            if (!IsReady) return;
            if (!_sessionLoadRaised && Volatile.Read(ref _sessionLoaded))
            {
                bool raise;
                lock (_stateLock)
                {
                    raise = !_disposed && _sessionLoaded && !_sessionLoadRaised;
                    if (raise) _sessionLoadRaised = true;
                }
                if (raise) RaiseSessionLoaded();
            }
            LimiterCore.Update();
        }

        /// <summary>
        /// Torch calls this on a plugin reload (PluginManager.ReloadPlugin and ReloadPlugins) and stops calling this
        /// instance's Update. This instance stops for good: its patches stay in the game, as it does not revert them
        /// (Torch's PatchManager could, through FreeContext and a Commit, but this plugin leaves them), and do nothing, and
        /// its commands, which Torch keeps, say so. The new instance never runs either: reloading one plugin Torch does
        /// not start it, and reloading all of them it does, but neither applies its patches nor tells it the world is
        /// loaded. So the limiter is off until Torch restarts.
        /// PluginManager.Detach, from TorchBase.Destroy, also disposes every plugin. Torch's own server never calls Destroy:
        /// a stop keeps the process and its plugins for the next start, and closing or restarting it ends the process without
        /// disposing them. A Torch that does call it is shutting down, not reloading: the limiter then only stops, and says
        /// nothing about a reload. <see cref="DisposedForShutdown"/> says which it is, and takes a reload when unsure.
        /// Either way a change still waiting for the autosave, or for its retry after a failed save, gets one last save, which
        /// Dispose waits for.
        /// </summary>
        public override void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;
                _sessionLoaded = false;
                _sessionLoadRaised = false;
            }

            if (_sessionManager != null)
                _sessionManager.SessionStateChanged -= SessionChanged;
            bool shutdown = DisposedForShutdown();
            if (shutdown)
                LimiterCore.OnSessionUnloading();
            else
                LimiterCore.Retire();
            _control?.OnSessionUnloading();
            if (!shutdown)
                Log.Warn(LimiterCore.RetiredMessage);
            _config?.Dispose();
            if (ReferenceEquals(Instance, this)) Instance = null;
            base.Dispose();
        }

        /// <summary>
        /// True when Torch disposes every plugin because it is shutting down, not reloading. Torch gives a plugin no flag for
        /// this: PluginManager.Detach, ReloadPlugin and ReloadPlugins all just call Dispose, and the only other differences are
        /// private fields. So the call stack is read: it counts as a shutdown only when PluginManager.Detach is on it and
        /// neither PluginManager.ReloadPlugin nor ReloadPlugins is. Any doubt, such as a Torch that renamed one of them, a
        /// subclass of PluginManager, or a frame the stack trace can't name, reads as a reload. That direction is safe: a
        /// reload taken for a shutdown would leave this instance's patches running on a config the new instance replaces,
        /// while a shutdown taken for a reload only logs a reload warning the admin doesn't need.
        /// </summary>
        private static bool DisposedForShutdown()
        {
            bool detach = false;
            foreach (StackFrame frame in new StackTrace(1, false).GetFrames() ?? Array.Empty<StackFrame>())
            {
                if (frame.GetMethod() is not { } method || method.DeclaringType != typeof(PluginManager))
                    continue;
                if (method.Name is nameof(PluginManager.ReloadPlugin) or nameof(PluginManager.ReloadPlugins))
                    return false;
                if (string.Equals(method.Name, nameof(PluginManager.Detach), StringComparison.Ordinal))
                    detach = true;
            }
            return detach;
        }

        private void SessionChanged(ITorchSession session, TorchSessionState state)
        {
            switch (state)
            {
                case TorchSessionState.Loaded:
                    // The limiter starts on the world now when the config is ready, else at the first Update once it is.
                    bool run;
                    lock (_stateLock)
                    {
                        if (_disposed || _sessionLoaded) return;
                        _sessionLoaded = true;
                        run = IsReady && !_sessionLoadRaised;
                        if (run) _sessionLoadRaised = true;
                    }
                    if (run) RaiseSessionLoaded();
                    break;

                case TorchSessionState.Unloading:
                    lock (_stateLock)
                    {
                        if (_disposed) return;
                        _sessionLoaded = false;
                        _sessionLoadRaised = false;
                    }
                    Log.Info("Session Unloading!");
                    LimiterCore.OnSessionUnloading();
                    _control?.OnSessionUnloading();
                    break;
            }
        }

        private static void RaiseSessionLoaded()
        {
            Log.Info("Session Loaded!");
            LimiterCore.OnSessionLoaded();
        }

        /// <summary>
        /// Instant kills and the memory limit always used Destroy before they had a setting. A file saved before then has no
        /// element for them, which XmlSerializer reads as the new TurnOff default, so it is set back to Destroy and saved.
        /// Only a config read from the file is checked, so a new config starts at TurnOff. Runs on the loaded instance before
        /// it is adopted; a save that fails here is taken over by the live instance's autosave.
        /// </summary>
        private static async Task KeepOlderDefaultsAsync(string configFile, ConfigFile config)
        {
            try
            {
                bool changed = false;
                if (await ConfigFileLacksAsync(configFile, nameof(Advanced_PB_LimiterConfig.InstantKillPunishment)).ConfigureAwait(false))
                {
                    config.Data.InstantKillPunishment = Enums.InstantPunishment.Destroy;
                    changed = true;
                }

                if (await ConfigFileLacksAsync(configFile, nameof(Advanced_PB_LimiterConfig.MemoryPunishment)).ConfigureAwait(false))
                {
                    config.Data.MemoryPunishment = Enums.InstantPunishment.Destroy;
                    changed = true;
                }

                if (!changed) return;
                // This save calls off the autosave the changes above armed, so the file is written once.
                await config.SaveAsync().ConfigureAwait(false);
                Log.Info("This config was saved before the instant kill and memory punishments had a setting: they stay at Destroy, as before. New configs start at TurnOff.");
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not check the config for the instant kill and memory punishments, they use the values read");
            }
        }

        /// <summary>
        /// True when the file has no element by this name under its root. XmlSerializer writes every value, so a missing
        /// element means the file was saved before the setting was added.
        /// </summary>
        private static async Task<bool> ConfigFileLacksAsync(string path, string element)
        {
            if (!File.Exists(path)) return false;
            XElement? root = Parse(await SafeFile.ReadAllBytesAsync(path).ConfigureAwait(false)).Root;
            return root is not null && root.Element(root.Name.Namespace + element) is null;
        }

        private static XDocument Parse(byte[] bytes)
        {
            using MemoryStream stream = new(bytes, false);
            using XmlReader reader = SafeXml.Reader(stream);
            return XDocument.Load(reader);
        }

        /// <summary>A save that fails is tried again by the autosave, unless saves are refused until Torch restarts.</summary>
        internal bool FailedSavesRetried => _config is { SavesRefused: false };

        /// <summary>
        /// Saves the config now. False when it was not saved, with the reason logged: the plugin is retired, the config is
        /// still loading, saves are refused until Torch restarts, or the write failed, which the autosave then tries again.
        /// </summary>
        public async Task<bool> SaveAsync()
        {
            if (LimiterCore.Retired)
            {
                // The file may already hold changes this stopped instance never saw.
                Log.Warn("Not saved: " + LimiterCore.RetiredMessage);
                return false;
            }

            if (!IsReady || _config is null)
            {
                Log.Warn("The config has not loaded yet, nothing was saved.");
                return false;
            }

            try
            {
                await _config.SaveAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception e)
            {
                Log.Warn(e, FailedSavesRetried
                    ? "The config could not be saved; it is tried again every 30 seconds until it saves"
                    : "The config was not saved");
                return false;
            }
        }
    }
}
