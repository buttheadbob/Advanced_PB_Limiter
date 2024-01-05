using NLog;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Timers;
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
using Advanced_PB_Limiter.Utils;
using Sandbox.ModAPI;
using Torch.Managers;
using Torch.Managers.PatchManager;

namespace Advanced_PB_Limiter
{
    public class Advanced_PB_Limiter : TorchPluginBase, IWpfPlugin
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const string CONFIG_FILE_NAME = "Advanced_PB_LimiterConfig.cfg";
        public static Advanced_PB_Limiter? Instance { get; private set; }
        private Advanced_PB_LimiterControl? _control;
        public UserControl GetControl() => _control ?? (_control = new Advanced_PB_LimiterControl());
        private Persistent<Advanced_PB_LimiterConfig>? _config;
        public Advanced_PB_LimiterConfig? Config => _config?.Data;
        public static Dispatcher Dispatcher { get; private set; } = Dispatcher.CurrentDispatcher;
        private static Timer NexusConnectionChecker { get; set; } = new Timer(10000);
        public static bool GameOnline { get; set; }
        public PatchManager? _pm;
        public PatchContext? _context;
        
        // Nexus stuff
        public static NexusAPI? nexusAPI { get; private set; }
        private static readonly Guid NexusGUID = new ("28a12184-0422-43ba-a6e6-2e228611cca5");
        public static bool NexusInstalled { get; private set; }
        // ReSharper disable once IdentifierTypo
        private static bool NexusInited;

        public override async void Init(ITorchBase torch)
        {
            base.Init(torch);
            SetupConfig();

            TorchSessionManager sessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (sessionManager != null)
                sessionManager.SessionStateChanged += SessionChanged;
            else
                Log.Warn("No session manager loaded!");

            await Save();
            
            Instance = this;
            _pm = DependencyProviderExtensions.GetManager<PatchManager>(torch.Managers);
            _context = _pm.AcquireContext();
            ProfilerPatch.Patch(_context);
            NexusConnectionChecker.Elapsed += CheckNexusConnection;
        }

        private void SessionChanged(ITorchSession session, TorchSessionState state)
        {
            switch (state)
            {
                case TorchSessionState.Loaded:
                    Log.Info("Session Loaded!");
                    ConnectNexus();
                    TrackingManager.Init();
                    if (NexusInited)
                        NexusConnectionChecker.Start();
                    GameOnline = true;
                    break;
                
                case TorchSessionState.Unloading:
                    Log.Info("Session Unloading!");
                    GameOnline = false;
                    break;
            }
        }
        
        private static void CheckNexusConnection(object sender, EventArgs e)
        {
            if (!NexusInstalled || !NexusInited || NexusManager.ThisServer is null) return;
            if (!NexusAPI.IsServerOnline(NexusManager.ThisServer.ServerID))
                NexusManager.RaiseNexusConnectedEvent(false);
        }
        
        private void ConnectNexus()
        {
            if (NexusInited) return;
            PluginManager? _pluginManager = Torch.Managers.GetManager<PluginManager>();
            if (_pluginManager is null)
                return;
                
            if (_pluginManager.Plugins.TryGetValue(NexusGUID, out ITorchPlugin? torchPlugin))
            {
                if (torchPlugin is null)
                    return;
                        
                Type? Plugin = torchPlugin.GetType();
                
                Type? NexusPatcher = Plugin != null! ? Plugin.Assembly.GetType("Nexus.API.PluginAPISync") : null;
                if (NexusPatcher != null)
                {
                    NexusPatcher.GetMethod("ApplyPatching", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[]
                    {
                        typeof(NexusAPI), "Advanced PB Limiter"
                    });
                    nexusAPI = new NexusAPI(8697);
                    MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(8697, NexusManager.HandleNexusMessage); 
                    NexusInstalled = true;
                    
                    NexusAPI.Server thisServer = NexusAPI.GetThisServer();
                    NexusManager.SetServerData(thisServer);
                    NexusManager.RaiseNexusConnectedEvent(true);
                }
            }
            NexusInited = true;
        }

        public async Task UpdateConfigFromNexus(Advanced_PB_LimiterConfig config)
        {
            _config = new Persistent<Advanced_PB_LimiterConfig>(Path.Combine(StoragePath, CONFIG_FILE_NAME), config);
             await Save();
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

        public Task Save()
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
            
            return Task.CompletedTask;
        }
    }
    
    
}
