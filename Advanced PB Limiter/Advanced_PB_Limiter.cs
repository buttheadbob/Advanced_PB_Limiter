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
using HarmonyLib;
using Nexus;
using Sandbox.ModAPI;
using Torch.Managers;
using Torch.Managers.PatchManager;
using VRage;

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
        private static Timer NexusConnectionChecker { get; set; } = new Timer(10000);
        public static bool GameOnline { get; set; }
        public PatchManager? _pm;
        public PatchContext? _context;
        public static PluginManager? _pluginManager;
        
        // Nexus stuff
        public NexusAPI? nexusAPI; 
        public static readonly Guid NexusGUID = new ("28a12184-0422-43ba-a6e6-2e228611cca5");
        public static ITorchPlugin? NexusPlugin;
        public static Type? Plugin;
        public Harmony _Harmony { get; } = new ("com.plugins.senx");
        public static bool NexusInstalled { get; private set; }
        // ReSharper disable once IdentifierTypo
        private static bool NexusInited;

        public override async void Init(ITorchBase torch)
        {
            base.Init(torch);
            SetupConfig();
            _Harmony.PatchAll();

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
            
            if (_pluginManager is null)
                return;
                
           
            nexusAPI = new (8697){CrossServerModID = 8697};
            
            _pluginManager.Plugins.TryGetValue(NexusGUID, out NexusPlugin);
            {
                if (NexusPlugin is null)
                    return;

                if (_context is not null)
                {
                    NexusMessageManager.afterRun_init(NexusPlugin);
                }
                        
                Plugin = NexusPlugin.GetType();
                if (Plugin is null) return;
                
                Type? NexusPatcher = Plugin.Assembly.GetType("Nexus.API.PluginAPISync");
                if (NexusPatcher != null)
                {
                    NexusPatcher.GetMethod("ApplyPatching", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[]
                    {
                        typeof(NexusAPI), "Advanced PB Limiter"
                    });
                    
                    MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(8697, NexusManager.HandleNexusMessage); 
                    NexusInstalled = true;
                    
                    NexusAPI.Server thisServer = NexusAPI.GetThisServer();
                    NexusManager.SetServerData(thisServer);
                    NexusManager.RaiseNexusConnectedEvent(true);
                }
            }
            NexusInited = true;
        }

        public Task UpdateConfigFromNexus(Advanced_PB_LimiterConfig _newConfig)
        {
            if (Instance is null || Instance.Config is null) return Task.CompletedTask;
            Advanced_PB_Limiter.UI_Dispatcher.Invoke(() =>
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
