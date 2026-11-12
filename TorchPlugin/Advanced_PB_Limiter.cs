using NLog;
using System;
using System.IO;
using System.Windows.Controls;
using System.Xml;
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
    public class Advanced_PB_Limiter : TorchPluginBase, IWpfPlugin, IPbLimiterHost
    {
        public static readonly Logger Log = LogManager.GetLogger("Advanced PB Limiter");
        private const string CONFIG_FILE_NAME = "Advanced_PB_LimiterConfig.cfg";
        public static Advanced_PB_Limiter? Instance { get; private set; }
        private Advanced_PB_LimiterControl? _control;
        public UserControl GetControl() => _control ??= new();
        private Persistent<Advanced_PB_LimiterConfig>? _config;
        public Advanced_PB_LimiterConfig? Config => _config?.Data;

        // IPbLimiterHost
        IPbLimiterConfig IPbLimiterHost.Config => Config!;
        void IPbLimiterHost.SaveConfig() => Save();
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

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            SetupConfig();
            Instance = this; // Before anything that can fail, the plugin's tab needs it either way.

            try
            {
                LimiterCore.Init(this);
            }
            catch (Exception e)
            {
                // A game update that changed something the limiter needs must not take the server down.
                Log.Fatal(e, "The game no longer matches this version, Advanced PB Limiter is inactive");
                return;
            }

            TorchSessionManager sessionManager = Torch.Managers.GetManager<TorchSessionManager>();

            if (sessionManager != null)
                sessionManager.SessionStateChanged += SessionChanged;
            else
                Log.Warn("No session manager loaded!");

            PatchManager patchManager = DependencyProviderExtensions.GetManager<PatchManager>(torch.Managers);
            ProfilerPatch_Torch.Patch(patchManager.AcquireContext());
        }

        public override void Update() => LimiterCore.Update();

        private void SessionChanged(ITorchSession session, TorchSessionState state)
        {
            switch (state)
            {
                case TorchSessionState.Loaded:
                    Log.Info("Session Loaded!");
                    LimiterCore.OnSessionLoaded();
                    break;

                case TorchSessionState.Unloading:
                    Log.Info("Session Unloading!");
                    LimiterCore.OnSessionUnloading();
                    break;
            }
        }

        private void SetupConfig()
        {
            string configFile = Path.Combine(StoragePath, CONFIG_FILE_NAME);

            try { _config = Persistent<Advanced_PB_LimiterConfig>.Load(configFile); }
            catch (Exception e) { Log.Warn(e); }

            if (_config?.Data != null)
            {
                KeepOlderDefaults(configFile, _config);
                return;
            }
            Log.Info("Create Default Config, because none was found!");

            _config = new(configFile, new());
            _config.Save();
        }

        /// <summary>
        /// Instant kills and the memory limit always used Destroy before they had a setting. A file saved before then has no
        /// element for them, which XmlSerializer reads as the new TurnOff default, so it is set back to Destroy and saved.
        /// Persistent.Load writes a missing file with the defaults first, so only a new config starts at TurnOff.
        /// </summary>
        private static void KeepOlderDefaults(string configFile, Persistent<Advanced_PB_LimiterConfig> config)
        {
            try
            {
                bool changed = false;
                if (ConfigFileLacks(configFile, nameof(Advanced_PB_LimiterConfig.InstantKillPunishment)))
                {
                    config.Data.InstantKillPunishment = Enums.Punishment.Destroy;
                    changed = true;
                }

                if (ConfigFileLacks(configFile, nameof(Advanced_PB_LimiterConfig.MemoryPunishment)))
                {
                    config.Data.MemoryPunishment = Enums.Punishment.Destroy;
                    changed = true;
                }

                if (!changed) return;
                config.Save();
                Log.Info("This config was saved before the instant kill and memory punishments had a setting: they stay at Destroy, as before. New configs start at TurnOff.");
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not check the config for the instant kill and memory punishments, they use the values read");
            }
        }

        /// <summary>
        /// True when the file has no element by this name under its root. Persistent writes every value, so a missing
        /// element means the file was saved before the setting was added.
        /// </summary>
        private static bool ConfigFileLacks(string path, string element)
        {
            if (!File.Exists(path)) return false;
            XmlDocument document = new();
            document.Load(path);
            return document.DocumentElement is not null && document.DocumentElement[element] is null;
        }

        public void Save()
        {
            try
            {
                _config?.Save();
                Log.Info("Configuration Saved.");
            }
            catch (Exception e)
            {
                Log.Warn(e, "Configuration failed to save");
            }
        }
    }
}
