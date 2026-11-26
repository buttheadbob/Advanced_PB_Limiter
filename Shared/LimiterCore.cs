#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
        Enums.InstantPunishment InstantKillPunishment { get; }
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
        Enums.InstantPunishment MemoryPunishment { get; }
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
        /// <summary>
        /// Runs a command's <paramref name="change"/> to the config at once, on the caller's thread, and makes sure the result
        /// is saved, once: the task gives false when it could not be, and the host has logged why. A host whose config saves
        /// each change as it is made answers with how that save went instead of writing the file, and logging a failure, a
        /// second time.
        /// </summary>
        Task<bool> ChangeConfigAsync(Action change);

        /// <summary>
        /// Why a command may not change the config now, or null when it may: the config is still being read, so a change
        /// would be overwritten by it.
        /// </summary>
        string? ConfigChangeRefusal { get; }
        void LogTrace(string message);
        void LogInfo(string message);
        void LogWarn(string message);
        void LogError(string message, Exception? exception = null);
    }

    public static class LimiterCore
    {
        private static IPbLimiterHost? _host;
        private static IPbLimiterConfig? _config;

        public static IPbLimiterHost Host => _host ?? throw NotInitialized();

        /// <summary>Read on every programmable block run, so it is cached rather than going through Host.</summary>
        public static IPbLimiterConfig Config => _config ?? throw NotInitialized();

        // Volatile: a Torch plugin reload clears it on its own thread, see Update.
        private static volatile bool _gameOnline;

        public static bool GameOnline
        {
            get => _gameOnline;
            private set => _gameOnline = value;
        }

        /// <summary>
        /// Stopped for good: a Torch plugin reload replaced this instance. Its patches stay in the game, as it does not
        /// revert them on a reload, and do nothing from then on. A new instance has statics of its own.
        /// </summary>
        public static bool Retired { get; private set; }

        /// <summary>The limiter is on: enabled in the config and not retired. What the patches check before doing anything.</summary>
        public static bool Active => !Retired && Config.Enabled;

        /// <summary>Why the limiter is retired, for the commands, the log and the plugin's tab.</summary>
        public static string RetiredMessage { get; private set; } = ReloadedMessage;

        private const string ReloadedMessage = "Advanced PB Limiter was reloaded, which Torch does not support: it is off until Torch restarts.";

        private const string MismatchMessage = "Advanced PB Limiter is inactive: the game no longer matches this version, as the log says. It stays off until Torch restarts with a version that matches.";

        private static long _offCount;
        private static bool _activeLastTick; // Game thread: the limiter was on at the last tick, so a disable is seen once.

        /// <summary>
        /// Goes up each time the limiter goes off: disabled in the config (counted by its Enabled setter, so an off and on
        /// again between two ticks counts too), found disabled at a tick after being on, retired, or its world unloading. A
        /// punishment waiting out its grace period compares it, so it is dropped when the limiter was off at any point
        /// meanwhile, even if it is on again by the end.
        /// </summary>
        public static long OffCount => Interlocked.Read(ref _offCount);

        /// <summary>
        /// Call from the config's Enabled setter before it turns <paramref name="config"/> off. Counted only for the config
        /// the limiter runs on: a copy a profile is tried on, or a config being read, turns nothing off. Any thread: Quasar,
        /// the Torch UI and the commands all set it.
        /// </summary>
        public static void CountOff(IPbLimiterConfig config)
        {
            if (ReferenceEquals(config, _config))
                Interlocked.Increment(ref _offCount);
        }

        /// <summary>Stops this instance for good, see <see cref="Retired"/>.</summary>
        public static void Retire()
        {
            RetiredMessage = ReloadedMessage;
            Retired = true;
            OnSessionUnloading();
        }

        /// <summary>
        /// Torch, when Init failed because the game no longer has something the limiter needs: nothing was patched, and the
        /// commands, which Torch registers anyway, answer that the limiter is inactive instead of acting as if it ran.
        /// </summary>
        public static void RetireMismatched()
        {
            RetiredMessage = MismatchMessage;
            Retired = true;
        }

        private static readonly LimiterLog Log = new("Core");
        private static long _tick;

        /// <summary>
        /// Call from the host's Init, before anything is patched. It may run before the config file is read: the limiter keeps
        /// the instance <see cref="IPbLimiterHost.Config"/> gives here, so the host copies the file's settings into that same
        /// instance and never replaces it.
        /// Throws when the game no longer has a member the limiter needs. The host must then stay inactive.
        /// </summary>
        public static void Init(IPbLimiterHost host)
        {
            _host = host;
            _config = host.Config;
            PbAccess.Init();
            ReplicationAccess.Init();
        }

        private static InvalidOperationException NotInitialized()
        {
            return new InvalidOperationException("Advanced PB Limiter was used before LimiterCore.Init.");
        }

        // Game thread, as Update. A failure that repeats every tick is logged once a minute, with how many came between.
        private static long _updateFailureLogged;
        private static int _updateFailuresSince;

        private static void LogUpdateFailure(Exception e)
        {
            long now = Stopwatch.GetTimestamp();
            if (_updateFailureLogged != 0 && now - _updateFailureLogged < Stopwatch.Frequency * 60)
            {
                _updateFailuresSince++;
                return;
            }
            Log.Error(e, _updateFailuresSince == 0
                ? "The update failed"
                : "The update failed, as it did " + InvariantText.Count(_updateFailuresSince, "more time", "more times") + " since the last of these lines");
            _updateFailureLogged = now;
            _updateFailuresSince = 0;
        }

        public static void OnSessionLoaded()
        {
            if (Retired) return;
            _tick = 0;
            PbProfiler.Init();
            GameOnline = true;
        }

        public static void OnSessionUnloading()
        {
            Interlocked.Increment(ref _offCount);
            GameOnline = false;
            TrackingManager.Stop();
            PunishmentManager.Clear();
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
                // Disabled since the last tick: see OffCount. The setter has counted it already; this also covers a config
                // replaced whole. Counted once when it goes off, not every tick it stays off.
                bool active = Active;
                if (_activeLastTick && !active)
                    Interlocked.Increment(ref _offCount);
                _activeLastTick = active;
                _tick++;
                PbProfiler.Update();
                TrackingManager.Update(_tick);
            }
            catch (Exception e)
            {
                LogUpdateFailure(e);
            }

            // A Torch plugin reload retires this instance on its own thread, and may have cleared the tracking data while
            // this tick ran: what the tick added since is cleared here, so nothing keeps blocks and their grids alive in the
            // retired assembly. Read after this tick's writes: Retire clears GameOnline before it clears the data, so either
            // its clear comes after them or this one does.
            Thread.MemoryBarrier();
            if (!GameOnline)
                TrackingManager.ClearAfterStop();
        }

        public static string GetThreadName()
        {
            return Thread.CurrentThread == MySandboxGame.Static.UpdateThread ? "UPDATE" : "BACKGROUND";
        }
    }
}
