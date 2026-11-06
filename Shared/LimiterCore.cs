using System;
using System.Threading;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Patches;
using Advanced_PB_Limiter.Utils;
using Sandbox;

// Loader neutral core, compiled into BOTH the Torch plugin and the Magnetar plugin.
// Everything under Shared/ uses game assemblies (Sandbox.*, VRage.*) only, with no Torch, NLog or PluginSdk references,
// and talks to the plugin that hosts it through IPbLimiterHost and IPbLimiterConfig.
namespace Advanced_PB_Limiter
{
    /// <summary>The settings both hosts expose (Torch: WPF and a .cfg file, Magnetar: Quasar and XML).</summary>
    public interface IPbLimiterConfig
    {
        bool Enabled { get; set; }
        bool AllowStaffCommands { get; set; }
        bool DebugReporting { get; set; }
        bool ShowTimingsBurp { get; set; }

        int RemoveInactivePBsAfterSeconds { get; }
        int RemovePlayersWithNoPBFrequencyInMinutes { get; }
        bool ClearHistoryOnRecompile { get; }
        int GracefulShutDownRequestDelay { get; }
        double InstantKillThreshold { get; }
        int PunishDamageAmount { get; }
        int GraceAfterOffence { get; }
        int MaxOffencesBeforePunishment { get; }
        int MaxCombinedOffencesBeforePunishment { get; }
        int OffenseDurationBeforeDeletion { get; }
        double MaxRunTimeMS { get; }
        double MaxRunTimeMSAvg { get; }
        Enums.Punishment Punishment { get; }
        bool CheckAllUserBlocksCombined { get; }
        bool PunishAllUserBlocksCombinedOnExcessLimits { get; }
        double MaxAllBlocksCombinedRunTimeMS { get; }
        double MaxAllBlocksCombinedRunTimeMSAvg { get; }
        bool IgnoreNPCs { get; }
        bool AllowSelfTurnOnExploit { get; }
        bool AllowNPCToAutoTurnOn { get; }
        bool WarnUserOnOffense { get; }
        bool TurnOffUnownedBlocks { get; }
        bool UseSimTime { get; }
        bool RequireRecompileOnRestart { get; }
        bool EnableMemoryMonitoring { get; }
        long PBMemoryThreshold { get; }
        bool KillProtectedBlock { get; }
        int StartupAllowance { get; }
        bool DeferRunAlways { get; }
        bool DeferRunOnSimspeedRate { get; }
        double DeferRunBelowSimRate { get; }
        int DeferRunCount { get; }
        bool DisableUpdate1 { get; }
        bool EnforceOnlyBelowSimThreshold { get; }
        double EnforceOnlyBelowSimRate { get; }

        bool TryGetPrivilegedPlayer(ulong steamId, out PrivilegedPlayer? player);

        /// <summary>Adds the privileged player, or replaces the one with the same SteamId.</summary>
        void SetPrivilegedPlayer(PrivilegedPlayer player);
    }

    /// <summary>What the core needs from whichever plugin loader it runs in.</summary>
    public interface IPbLimiterHost
    {
        IPbLimiterConfig Config { get; }
        string Version { get; }
        void SaveConfig();
        void LogTrace(string message);
        void LogInfo(string message);
        void LogWarn(string message);
        void LogError(string message, Exception? exception = null);
    }

    public static class LimiterCore
    {
        public static IPbLimiterHost Host { get; private set; } = null!;

        /// <summary>Read on every programmable block run, so it is cached rather than going through Host.</summary>
        public static IPbLimiterConfig Config { get; private set; } = null!;

        public static bool GameOnline { get; private set; }

        private static readonly LimiterLog Log = new("Core");
        private static long _tick;

        /// <summary>
        /// Call from the host's Init, after its config is loaded and before anything is patched.
        /// Throws when the game no longer has a member the limiter needs. The host must then stay inactive.
        /// </summary>
        public static void Init(IPbLimiterHost host)
        {
            Host = host;
            Config = host.Config;
            PbAccess.Init();
            ReplicationAccess.Init();
        }

        public static void OnSessionLoaded()
        {
            _tick = 0;
            PbProfiler.Init();
            GameOnline = true;
        }

        public static void OnSessionUnloading()
        {
            GameOnline = false;
            TrackingManager.Stop();
            PbProfiler.Stop();
            ReportManager.Clear();
        }

        /// <summary>Call from the host's Update, which the game runs once per tick on the game thread.</summary>
        public static void Update()
        {
            if (!GameOnline) return;

            // Magnetar does not guard this call. An exception here would take the server down.
            try
            {
                _tick++;
                PbProfiler.Update();
                TrackingManager.Update(_tick);
            }
            catch (Exception e)
            {
                Log.Error(e, "The update failed");
            }
        }

        public static string GetThreadName()
        {
            return Thread.CurrentThread == MySandboxGame.Static.UpdateThread ? "UPDATE" : "BACKGROUND";
        }
    }
}
