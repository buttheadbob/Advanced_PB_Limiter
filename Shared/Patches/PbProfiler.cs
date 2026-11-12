using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;

namespace Advanced_PB_Limiter.Patches
{
    /// <summary>
    /// What the limiter does around a programmable block run. Each host patches MyProgrammableBlock its own way
    /// (Torch: PatchManager, Magnetar: Harmony) and calls these from thin prefixes and suffixes.
    /// These run for every script on every run. Nothing on the normal path may allocate, and that includes lambdas:
    /// one that captures a parameter allocates on every call, even when it is never reached.
    /// The run timing and the garbage collection check started from HaE PBLimiter by sirhamsteralot (MIT), and have been heavily changed since.
    /// </summary>
    public static class PbProfiler
    {
        private const int UPDATE1_RUNS_PER_UPDATE10 = 10;
        private const int DISABLED_BLOCK_SHUTDOWN_DELAY_SECONDS = 40; // Gives the freshly started server time to stabilize.
        private const int IGNORE_RUNS_AFTER_SAVE_SECONDS = 10;

        private sealed class DeferState
        {
            public int Update1;
            public int Update10;
            public int Update100;
        }

        private static readonly LimiterLog Log = new("Runtime Processor");
        private static IPbLimiterConfig Config => LimiterCore.Config;

        // Blocks instantiate while the world loads, before the session reports loaded, so this collects from the start.
        private static readonly ConcurrentDictionary<long, MyProgrammableBlock> DisabledProgrammableBlocks = new();
        private static bool _collectDisabledBlocks = true;
        private static bool _disabledBlockShutdownPending;
        private static long _disabledBlockShutdownTick;

        // Weak keys, so this forgets a block when the game does.
        private static readonly ConditionalWeakTable<MyProgrammableBlock, DeferState> DeferStatus = new();

        private static bool _serverSaved;
        private static long _lastSaveTick;

        // Set by BeforeRun when it skips a run and cleared by the AfterRun that belongs to it, which runs next.
        [ThreadStatic]
        private static bool _runSkipped;

        // A collection pauses every thread, so it would show up as run time the script did not use.
        // Collecting a generation also collects the ones below it, so generation 0 sees all of them.
        private static int _gcCount;

        public static void Init()
        {
            MySession.OnSaved += OnWorldSaved;

            if (Config.AllowSelfTurnOnExploit || Config.RequireRecompileOnRestart) return;

            if (Config.DebugReporting)
                Log.Warn($"Shutting down all pb's that were off during startup in {DISABLED_BLOCK_SHUTDOWN_DELAY_SECONDS} seconds...");

            _disabledBlockShutdownTick = Stopwatch.GetTimestamp() + DISABLED_BLOCK_SHUTDOWN_DELAY_SECONDS * Stopwatch.Frequency;
            _disabledBlockShutdownPending = true;
        }

        public static void Stop()
        {
            MySession.OnSaved -= OnWorldSaved;
            _collectDisabledBlocks = true; // Ready for the next session to load.
            _disabledBlockShutdownPending = false;
            _serverSaved = false;
            DisabledProgrammableBlocks.Clear();
        }

        // MySession.ServerSaving cannot be polled from a tick: the game sets and clears it inside one Save call.
        private static void OnWorldSaved(bool success, string path)
        {
            _lastSaveTick = Stopwatch.GetTimestamp();
            _serverSaved = true;

            if (Config.DebugReporting)
                Log.Info($"World saved, ignoring run times for {IGNORE_RUNS_AFTER_SAVE_SECONDS} seconds.");
        }

        /// <summary>Once per tick, on the game thread.</summary>
        public static void Update()
        {
            if (_serverSaved && _lastSaveTick.TicksTillNow_TimeSpan().TotalSeconds > IGNORE_RUNS_AFTER_SAVE_SECONDS)
                _serverSaved = false;

            if (_disabledBlockShutdownPending && Stopwatch.GetTimestamp() >= _disabledBlockShutdownTick)
            {
                _disabledBlockShutdownPending = false;
                _collectDisabledBlocks = false; // Startup is over, a block that is off from here on is off on purpose.
                ShutdownPb();
            }
        }

        private static void ShutdownPb()
        {
            if (!Config.Enabled)
            {
                DisabledProgrammableBlocks.Clear();
                return;
            }

            if (Config.DebugReporting)
                Log.Info("Shutting down all pb's that should not be running....");

            foreach (KeyValuePair<long, MyProgrammableBlock> kvp in DisabledProgrammableBlocks)
            {
                if (kvp.Value.Closed || kvp.Value.MarkedForClose) continue;

                if (Config.DebugReporting)
                    Log.Warn("Shutting down: " + kvp.Value.CustomName);

                kvp.Value.Enabled = false;
            }

            DisabledProgrammableBlocks.Clear();
        }

        /// <summary>Before MyProgrammableBlock.ExecuteCode. Returns false to skip the run.</summary>
        public static bool BeforeExecuteCode(MyProgrammableBlock? __instance, ref UpdateType updateSource, ref string response)
        {
            if (!Config.Enabled) return true;
            if (__instance == null) return true;
            response = string.Empty;
            _gcCount = GC.CollectionCount(0);

            bool defer = Config.DeferRunAlways || (Config.DeferRunOnSimspeedRate && Sync.ServerSimulationRatio < Config.DeferRunBelowSimRate);
            bool disableUpdate1 = Config.DisableUpdate1;
            if (!defer && !disableUpdate1) return true;

            // Only timed runs are deferred. A button, timer, antenna or mod asked for this run, and so did the limiter
            // when it sends GracefulShutDown, and none of those would ever be retried.
            const UpdateType timedRuns = UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100;
            if ((updateSource & timedRuns) == 0) return true;

            long time = Config.ShowTimingsBurp ? Stopwatch.GetTimestamp() : 0;

            // This would only not exist on first run, which is the instantiation run and should go without intervention.
            if (!DeferStatus.TryGetValue(__instance, out DeferState? deferState))
            {
                DeferStatus.Add(__instance, new DeferState());
                return true;
            }

            bool allow = false;
            if ((updateSource & UpdateType.Update1) != 0)
            {
                if (disableUpdate1)
                {
                    // Update1 runs only when Update10 would, so one run in ten counts as an Update10 from here on.
                    if (++deferState.Update1 >= UPDATE1_RUNS_PER_UPDATE10)
                    {
                        deferState.Update1 = 0;
                        updateSource |= UpdateType.Update10;
                    }
                }
                else
                {
                    allow |= AllowDeferredRun(defer, ref deferState.Update1);
                }
            }

            if ((updateSource & UpdateType.Update10) != 0)
                allow |= AllowDeferredRun(defer, ref deferState.Update10);

            if ((updateSource & UpdateType.Update100) != 0)
                allow |= AllowDeferredRun(defer, ref deferState.Update100);

            if (Config.ShowTimingsBurp)
                Log.Info($"Defer Manager took {time.TicksTillNow_TimeSpan().TotalMilliseconds:N5}ms for {__instance.CustomName}.");

            return allow;
        }

        private static bool AllowDeferredRun(bool defer, ref int count)
        {
            if (!defer) return true;
            if (++count < Config.DeferRunCount) return false;
            count = 0;
            return true;
        }

        /// <summary>Before MyProgrammableBlock.RunSandboxedProgramActionCore. Returns false to skip the run.</summary>
        public static bool BeforeRun(MyProgrammableBlock? __instance)
        {
            if (__instance == null) return true; // Something funky is going on so we won't track it this pass but don't block it either.
            if (__instance.Enabled) return true;
            if (!Config.Enabled) return true;
            if (Config.AllowSelfTurnOnExploit) return true;

            if (Config.RequireRecompileOnRestart)
            {
                if (Config.DebugReporting)
                    Log.Warn("Skipping recompile for pb: " + __instance.CustomName);

                _runSkipped = true;
                return false;
            }

            if (_collectDisabledBlocks && DisabledProgrammableBlocks.TryAdd(__instance.EntityId, __instance))
            {
                if (Config.DebugReporting)
                    Log.Warn($"Programmable Block {__instance.CustomName} was not enabled during restart and will be disabled again shortly.");
            }

            return true;
        }

        /// <summary>After MyProgrammableBlock.RunSandboxedProgramActionCore, including when BeforeRun skipped it.</summary>
        public static void AfterRun(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            if (_runSkipped)
            {
                _runSkipped = false;
                response ??= "";
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
                return;
            }

            if (!Config.Enabled) return;
            if (__instance == null) return; // Something funky is going on, so we won't track it this pass but don't block it either.

            // the pb will still run up to the point of executing the script even when disabled by script termination fault, this will stop some of that.
            // also stops the limiter from checking the block.
            if (__result != MyProgrammableBlock.ScriptTerminationReason.None)
            {
                TurnOffFaultedBlock(__instance, __result);
                return;
            }

            if (_gcCount != GC.CollectionCount(0))
            {
                if (Config.DebugReporting) Log.Warn("GC Occured.  Not reporting runtime.");
                return;
            }

            if (_serverSaved) return;

            // 0 when this was the script's constructor or Save() rather than a run of Main().
            double runtime = PbAccess.GetRuntime(__instance).LastRunTimeMs;
            if (runtime <= 0) return;

            long debugStamp = Config.ShowTimingsBurp ? Stopwatch.GetTimestamp() : 0;

            // When sim speed tanks a script takes longer to run at no fault of the player, so its time counts for less.
            if (Config.UseSimTime)
            {
                float simulationRatio = Sync.ServerSimulationRatio;
                if (simulationRatio < 1)
                    runtime *= simulationRatio;
            }

            TrackingManager.UpdateTrackingData(__instance, runtime);

            if (Config.ShowTimingsBurp)
                Log.Info($"[{LimiterCore.GetThreadName()} THREAD] Tracked PB[{__instance.CustomName}] Runtime in: {debugStamp.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N6", CultureInfo.InvariantCulture)}ms");
        }

        // On its own so the lambda's capture does not allocate on every run. Rarely reached, the script is already dead.
        private static void TurnOffFaultedBlock(MyProgrammableBlock block, MyProgrammableBlock.ScriptTerminationReason reason)
        {
            MySandboxGame.Static.Invoke(() => { block.Enabled = false; }, "Advanced PB Limiter");

            if (Config.DebugReporting)
                Log.Info($"{block.CustomName} Script Termination Reason:{reason}");
        }

        /// <summary>Before MyProgrammableBlock.Compile. Returns the value to hand to AfterRecompile.</summary>
        public static bool BeforeRecompile(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("PB is null!");
                return false;
            }

            TrackingManager.PBRecompiled(__instance);
            return !__instance.Enabled && !Config.AllowSelfTurnOnExploit;
        }

        /// <summary>After MyProgrammableBlock.Compile.</summary>
        public static void AfterRecompile(MyProgrammableBlock? __instance, bool keepDisabled)
        {
            if (__instance is null) return;
            if (keepDisabled) __instance.Enabled = false;
        }

        /// <summary>
        /// Before MyProgrammableBlock.UpdateStorage. Returns false to skip it, in which case the Storage copy it would make
        /// is made here, so only the script's Save() is left out.
        /// </summary>
        public static bool BeforeSave(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("Null programmable block instance using the Save() method.. interesting.  If this happens a lot, let SentorX know please.");
                return true;
            }

            if (__instance.Enabled) return true;
            if (!Config.Enabled) return true;

            bool isNpc = MySession.Static.Players.IdentityIsNpc(__instance.OwnerId);
            if (!isNpc && !Config.AllowSelfTurnOnExploit)
            {
                if (Config.DebugReporting)
                {
                    MyIdentity? player = MySession.Static.Players.TryGetIdentity(__instance.OwnerId);
                    if (player is not null)
                        Log.Warn($"{player.DisplayName} ({__instance.OwnerId}) tried to save a programmable block [{__instance.CustomName}] while it was turned off.  This is has been disabled.");
                }
                PbAccess.CopyStorage(__instance);
                return false;
            }

            if (isNpc && !Config.AllowNPCToAutoTurnOn)
            {
                PbAccess.CopyStorage(__instance);
                return false;
            }

            return true;
        }
    }
}
