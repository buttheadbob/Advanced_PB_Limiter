using NLog;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
using Nexus;
using NLog.Config;
using Sandbox;
using Sandbox.ModAPI;
using Torch.Managers;
using Torch.Managers.PatchManager;
using Timer = System.Timers.Timer;

namespace Advanced_PB_Limiter
{
    public class Advanced_PB_Limiter : TorchPluginBase, IWpfPlugin
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const string CONFIG_FILE_NAME = "Advanced_PB_LimiterConfig.cfg";
        public static Advanced_PB_Limiter? Instance { get; private set; }
        private Advanced_PB_LimiterControl? _control;
        public UserControl GetControl() => _control ?? (_control = new Advanced_PB_LimiterControl());
        private Persistent<Advanced_PB_LimiterConfig>? _config;
        public Advanced_PB_LimiterConfig? Config => _config?.Data;
        public static Dispatcher UI_Dispatcher { get; set; } = Dispatcher.CurrentDispatcher;
        private static Thread? UpdateThread { get; set; }
        private static Thread? MainThread { get; set; }
        private static Thread? DrawThread { get; set; }
        private static Timer NexusConnectionChecker { get; set; } = new (10000);
        public static bool GameOnline { get; set; }
        public PatchManager? _pm;
        public PatchContext? _context;
        public static PluginManager? _pluginManager;
        
        // Nexus stuff
        public NexusAPI? nexusAPI; 
        public static readonly Guid NexusGUID = new ("28a12184-0422-43ba-a6e6-2e228611cca5");
        public static ITorchPlugin? NexusPlugin;
        public static Type? Plugin;
        public static NexusAPI.Server? ThisServer;
        public static bool NexusInstalled { get; private set; }
        private static int TickUpdateCount = 0;

        public override async void Init(ITorchBase torch)
        {
            base.Init(torch);
            SetupConfig();

            TorchSessionManager sessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            _pluginManager = Torch.Managers.GetManager<PluginManager>();
            
            if (sessionManager != null)
                sessionManager.SessionStateChanged += SessionChanged;
            else
                Log.Warn("No session manager loaded!");

            await Save();
            
            Instance = this;
            _pm = DependencyProviderExtensions.GetManager<PatchManager>(torch.Managers);
            _context = _pm.AcquireContext();
            ProfilerPatch_Torch.Patch(_context);
            
            NexusConnectionChecker.Elapsed += (o, args) => ConnectNexus();
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
                    ConnectNexus();
                    TrackingManager.Start();
                    if (!NexusInstalled)
                        NexusConnectionChecker.Start();
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
        
        private void ConnectNexus()
        {
            if (NexusInstalled) return;
            
            if (_pluginManager is null)
                return;
           
            nexusAPI = new NexusAPI(8697){CrossServerModID = 8697};
            
            _pluginManager.Plugins.TryGetValue(NexusGUID, out NexusPlugin);
            {
                if (NexusPlugin is null)
                    return;
                        
                Plugin = NexusPlugin.GetType();
                
                Type? NexusPatcher = Plugin.Assembly.GetType("Nexus.API.PluginAPISync");
                if (NexusPatcher != null)
                {
                    NexusPatcher.GetMethod("ApplyPatching", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[]
                    {
                        typeof(NexusAPI), "Advanced PB Limiter"
                    });
                    
                    MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(8697, NexusNetworkManager.HandleReceivedMessage); 
                    NexusInstalled = true;
                    ThisServer = NexusAPI.GetThisServer();
                }
            }
            NexusInstalled = true;
            NexusConnectionChecker.Stop();
            Log.Info("Connected to Nexus v2");
        }

        public Task UpdateConfigFromNexus(Advanced_PB_LimiterConfig _newConfig)
        {
            if (Instance?.Config is null) return Task.CompletedTask;
            UI_Dispatcher.Invoke(() =>
            {
                Instance.Config.Enabled = _newConfig.Enabled;
            Instance.Config.AllowStaffCommands = _newConfig.AllowStaffCommands;
            Instance.Config.RemovePlayersWithNoPBFrequencyInMinutes = _newConfig.RemovePlayersWithNoPBFrequencyInMinutes;
            Instance.Config.MaxRunsToTrack = _newConfig.MaxRunsToTrack;
            Instance.Config.ClearHistoryOnRecompile = _newConfig.ClearHistoryOnRecompile;
            Instance.Config.GracefulShutDownRequestDelay = _newConfig.GracefulShutDownRequestDelay;
            Instance.Config.PrivilegedPlayers = _newConfig.PrivilegedPlayers; //
            Instance.Config.InstantKillThreshold = _newConfig.InstantKillThreshold;
            Instance.Config.PunishDamageAmount = _newConfig.PunishDamageAmount;
            Instance.Config.GraceAfterOffence = _newConfig.GraceAfterOffence;
            Instance.Config.MaxOffencesBeforePunishment = _newConfig.MaxOffencesBeforePunishment;
            Instance.Config.OffenseDurationBeforeDeletion = _newConfig.OffenseDurationBeforeDeletion;
            Instance.Config.MaxRunTimeMS = _newConfig.MaxRunTimeMS;
            Instance.Config.MaxRunTimeMSAvg = _newConfig.MaxRunTimeMSAvg;
            Instance.Config.Punishment = _newConfig.Punishment;
            Instance.Config.CheckAllUserBlocksCombined = _newConfig.CheckAllUserBlocksCombined;
            Instance.Config.PunishAllUserBlocksCombinedOnExcessLimits = _newConfig.PunishAllUserBlocksCombinedOnExcessLimits;
            Instance.Config.MaxAllBlocksCombinedRunTimeMS = _newConfig.MaxAllBlocksCombinedRunTimeMS;
            Instance.Config.MaxAllBlocksCombinedRunTimeMSAvg = _newConfig.MaxAllBlocksCombinedRunTimeMSAvg;
            Instance.Config.IgnoreNPCs = _newConfig.IgnoreNPCs;
            Instance.Config.AllowSelfTurnOnExploit = _newConfig.AllowSelfTurnOnExploit;
            Instance.Config.AllowNPCToAutoTurnOn = _newConfig.AllowNPCToAutoTurnOn;
            Instance.Config.WarnUserOnOffense = _newConfig.WarnUserOnOffense;
            Instance.Config.TurnOffUnownedBlocks = _newConfig.TurnOffUnownedBlocks;
            Instance.Config.UseSimTime = _newConfig.UseSimTime;
            });
            
            return Task.CompletedTask;
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
