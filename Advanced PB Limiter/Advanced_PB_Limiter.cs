using NLog;
using System;
using System.IO;
using System.Windows.Controls;
using Advanced_PB_Limiter.Manager;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Session;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.UI;

namespace Advanced_PB_Limiter
{
    public class Advanced_PB_Limiter : TorchPluginBase, IWpfPlugin
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const string CONFIG_FILE_NAME = "Advanced_PB_LimiterConfig.cfg";
        public static Advanced_PB_Limiter Instance { get; private set; }

        private Advanced_PB_LimiterControl _control;
        public UserControl GetControl() => _control ?? (_control = new Advanced_PB_LimiterControl());

        private Persistent<Advanced_PB_LimiterConfig> _config;
        public Advanced_PB_LimiterConfig Config => _config?.Data;

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            SetupConfig();

            TorchSessionManager sessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (sessionManager != null)
                sessionManager.SessionStateChanged += SessionChanged;
            else
                Log.Warn("No session manager loaded!");

            Save();
            
            Instance = this;
        }

        private void SessionChanged(ITorchSession session, TorchSessionState state)
        {
            switch (state)
            {
                case TorchSessionState.Loaded:
                    Log.Info("Session Loaded!");
                    TrackingManager.Init();
                    break;

                case TorchSessionState.Unloading:
                    Log.Info("Session Unloading!");
                    break;
            }
        }

        private void SetupConfig()
        {
            string configFile = Path.Combine(StoragePath, CONFIG_FILE_NAME);

            try { _config = Persistent<Advanced_PB_LimiterConfig>.Load(configFile); }
            catch (Exception e) { Log.Warn(e); }

            if (_config?.Data == null)
            {
                Log.Info("Create Default Config, because none was found!");

                _config = new Persistent<Advanced_PB_LimiterConfig>(configFile, new Advanced_PB_LimiterConfig());
                _config.Save();
            }
        }

        public void Save()
        {
            try
            {
                _config.Save();
                Log.Info("Configuration Saved.");
            }
            catch (IOException e)
            {
                Log.Warn(e, "Configuration failed to save");
            }
        }
    }
}
