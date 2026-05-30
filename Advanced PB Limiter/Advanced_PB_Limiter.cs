using NLog;
using System;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Patches;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Session;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.UI;
using Sandbox;
using Torch.Managers;
using Torch.Managers.PatchManager;

namespace Advanced_PB_Limiter
{
    public class Advanced_PB_Limiter : TorchPluginBase, IWpfPlugin
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const string CONFIG_FILE_NAME = "Advanced_PB_LimiterConfig.cfg";
        public static Advanced_PB_Limiter? Instance { get; private set; }
        private Advanced_PB_LimiterControl? _control;
        public UserControl GetControl() => _control ??= new();
        private Persistent<Advanced_PB_LimiterConfig>? _config;
        public Advanced_PB_LimiterConfig? Config => _config?.Data;
        public static Dispatcher UI_Dispatcher { get; set; } = Dispatcher.CurrentDispatcher;
        private static Thread? UpdateThread { get; set; }
        private static Thread? MainThread { get; set; }
        private static Thread? DrawThread { get; set; }
        public static bool GameOnline { get; set; }
        public PatchManager? _pm;
        public PatchContext? _context;
        public static PluginManager? _pluginManager;

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            SetupConfig();

            TorchSessionManager sessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            _pluginManager = Torch.Managers.GetManager<PluginManager>();
            
            if (sessionManager != null)
                sessionManager.SessionStateChanged += SessionChanged;
            else
                Log.Warn("No session manager loaded!");
            
            Instance = this;
            _pm = DependencyProviderExtensions.GetManager<PatchManager>(torch.Managers);
            _context = _pm.AcquireContext();
            ProfilerPatch_Torch.Patch(_context);
        }

        public string GetThreadName()
        {
            if (Thread.CurrentThread == MainThread) return "MAIN";
            if (Thread.CurrentThread == UpdateThread) return "UPDATE";
            if (Thread.CurrentThread == DrawThread) return "DRAW";
            return "BACKGROUND";
        }

        private void SessionChanged(ITorchSession session, TorchSessionState state)
        {
            switch (state)
            {
                case TorchSessionState.Loaded:
                    Log.Info("Session Loaded!");
                    UpdateThread = MySandboxGame.Static.UpdateThread;
                    MySandboxGame.Static.Invoke(()=> {MainThread = Thread.CurrentThread;}, "Advanced PB Limiter");
                    TrackingManager.Start();
                    GameOnline = true;
                    ProfilerPatch_Torch.Init();
                    break;
                
                case TorchSessionState.Unloading:
                    Log.Info("Session Unloading!");
                    GameOnline = false;
                    TrackingManager.Stop();
                    break;
            }
        }
        
        private void SetupConfig()
        {
            string configFile = Path.Combine(StoragePath, CONFIG_FILE_NAME);

            try { _config = Persistent<Advanced_PB_LimiterConfig>.Load(configFile); }
            catch (Exception e) { Log.Warn(e); }

            if (_config?.Data != null) return;
            Log.Info("Create Default Config, because none was found!");

            _config = new(configFile, new());
            _config.Save();
        }

        public void Save()
        {
            try
            {
                _config?.Save();
                Log.Info("Configuration Saved.");
            }
            catch (IOException e)
            {
                Log.Warn(e, "Configuration failed to save");
            }
        }
    }
}
