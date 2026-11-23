#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
        /// <summary>
        /// Taken when this plugin's Compile prefix runs and handed to its postfix. A prefix of another plugin that skips
        /// the original can run before this one, in which case this prefix never runs and the postfix sees
        /// <see cref="Started"/> false: no compile of ours to finish.
        /// </summary>
        public readonly struct RecompileStamp
        {
            public RecompileStamp(bool keepDisabled)
            {
                Started = true;
                KeepDisabled = keepDisabled;
            }

            public bool Started { get; }
            public bool KeepDisabled { get; }
        }

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


        public static void Init()
        {
            MySession.OnSaved += OnWorldSaved;

            if (Config.AllowSelfTurnOnExploit || Config.RequireRecompileOnRestart)
            {
                // No startup shutdown this session: the blocks collected for it are let go, and none are collected later
                // if these settings are turned off meanwhile.
                _collectDisabledBlocks = false;
                DisabledProgrammableBlocks.Clear();
                return;
            }

            if (Config.DebugReporting)
                Log.Warn($"Shutting down all programmable blocks that were off during startup in {DISABLED_BLOCK_SHUTDOWN_DELAY_SECONDS} seconds...");

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
            if (!LimiterCore.Active)
            {
                DisabledProgrammableBlocks.Clear();
                return;
            }

            if (Config.DebugReporting)
                Log.Info("Shutting down all programmable blocks that should not be running.");

            MyProgrammableBlock[] blocks = DisabledProgrammableBlocks.Values.ToArray();
            for (int index = 0; index < blocks.Length; index++)
            {
                MyProgrammableBlock block = blocks[index];
                if (block.Closed || block.MarkedForClose) continue;

                if (Config.DebugReporting)
                    Log.Warn("Shutting down: " + block.ReadName());

                block.Enabled = false;
            }

            DisabledProgrammableBlocks.Clear();
        }

        /// <summary>Before MyProgrammableBlock.ExecuteCode. Returns false to skip the run.</summary>
        public static bool BeforeExecuteCode(MyProgrammableBlock? __instance, ref UpdateType updateSource, ref string response)
        {
            if (!LimiterCore.Active) return true;
            if (__instance == null) return true;
            response = string.Empty;

            bool defer = Config.DeferRunAlways || (Config.DeferRunOnSimspeedRate && Sync.ServerSimulationRatio < Config.DeferRunBelowSimRate);
            bool disableUpdate1 = Config.DisableUpdate1;
            if (!defer && !disableUpdate1) return true;

            // Only timed runs are deferred. A button, timer, antenna or mod asked for this run, and so did the limiter
            // when it sends GracefulShutDown, and none of those would ever be retried.
            const UpdateType timedRuns = UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100;
            if (!HasAny(updateSource, timedRuns)) return true;

            long time = Config.ShowTimingsBurp ? Stopwatch.GetTimestamp() : 0;

            // This would only not exist on first run, which is the instantiation run and should go without intervention.
            if (!DeferStatus.TryGetValue(__instance, out DeferState? deferState))
            {
                DeferStatus.Add(__instance, new DeferState());
                return true;
            }

            bool allow = false;
            if (HasAny(updateSource, UpdateType.Update1))
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

            if (HasAny(updateSource, UpdateType.Update10))
                allow |= AllowDeferredRun(defer, ref deferState.Update10);

            if (HasAny(updateSource, UpdateType.Update100))
                allow |= AllowDeferredRun(defer, ref deferState.Update100);

            if (Config.ShowTimingsBurp)
                Log.Info($"Defer Manager took {time.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N5", CultureInfo.InvariantCulture)}ms for {__instance.ReadName()}.");

            return allow;
        }

        // Runs for every script run, so the bits are compared as numbers: Enum.HasFlag would box both values on .NET
        // Framework, which Legacy and Torch run on.
        private static bool HasAny(UpdateType value, UpdateType flags) => ((int)value & (int)flags) != 0;

        private static bool AllowDeferredRun(bool defer, ref int count)
        {
            if (!defer) return true;
            if (++count < Config.DeferRunCount) return false;
            count = 0;
            return true;
        }

        /// <summary>Before MyProgrammableBlock.RunSandboxedProgramActionCore. Returns false to skip the run.</summary>
        /// <param name="__instance">The block about to run.</param>
        /// <param name="stamp">Handed to AfterRun, see RunClock. Taken last, so this method's own work is not in it.</param>
        public static bool BeforeRun(MyProgrammableBlock? __instance, out RunStamp stamp)
        {
            stamp = default;
            if (__instance == null) return true; // Something funky is going on so we won't track it this pass but don't block it either.
            if (!LimiterCore.Active || __instance.Enabled || Config.AllowSelfTurnOnExploit)
            {
                stamp = Stamp(__instance);
                return true;
            }

            if (Config.RequireRecompileOnRestart)
            {
                if (Config.DebugReporting)
                    Log.Warn("Skipping recompile for pb: " + __instance.ReadName());

                _runSkipped = true;
                return false;
            }

            if (_collectDisabledBlocks && DisabledProgrammableBlocks.TryAdd(__instance.EntityId, __instance))
            {
                if (Config.DebugReporting)
                    Log.Warn($"Programmable Block {__instance.ReadName()} was not enabled during restart and will be disabled again shortly.");
            }

            stamp = Stamp(__instance);
            return true;
        }

        private static RunStamp Stamp(MyProgrammableBlock block)
        {
            IMyGridProgramRuntimeInfo runtime = PbAccess.GetRuntime(block);
            return RunClock.Start(PbAccess.GetProgram(block), runtime.LastRunTimeMs, runtime.TimeSinceLastRun.Ticks == 0);
        }

        /// <summary>After MyProgrammableBlock.RunSandboxedProgramActionCore, including when BeforeRun skipped it.</summary>
        public static void AfterRun(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result, in RunStamp stamp)
        {
            // First, for every stamped run, so a run nested in another through TryRun is taken off the outer one. 0 when this
            // was not a run of Main(): the constructor, Save() and the creation of the script's instance are not measured.
            bool collected = false;
            double runtime = 0;
            if (__instance is not null)
            {
                IMyGridProgramRuntimeInfo info = PbAccess.GetRuntime(__instance);
                runtime = RunClock.Finish(in stamp, info.LastRunTimeMs, PbAccess.GetProgram(__instance), info.TimeSinceLastRun.Ticks == 0, out collected);
            }

            if (_runSkipped)
            {
                _runSkipped = false;
                response ??= "";
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
                return;
            }

            // A prefix of another plugin skipped the call before ours could stamp it, so Finish has only the block's last
            // run figure to return. No run happened, so none may be recorded or punished.
            if (!stamp.Started) return;

            if (!LimiterCore.Active) return;
            if (__instance == null) return; // Something funky is going on, so we won't track it this pass but don't block it either.

            // the pb will still run up to the point of executing the script even when disabled by script termination fault, this will stop some of that.
            // also stops the limiter from checking the block.
            if (__result != MyProgrammableBlock.ScriptTerminationReason.None)
            {
                TurnOffFaultedBlock(__instance, __result);
                return;
            }

            if (_serverSaved) return;
            if (runtime <= 0) return;

            long debugStamp = Config.ShowTimingsBurp ? Stopwatch.GetTimestamp() : 0;

            // When sim speed tanks a script takes longer to run at no fault of the player, so its time counts for less.
            if (Config.UseSimTime)
            {
                float simulationRatio = Sync.ServerSimulationRatio;
                if (simulationRatio < 1)
                    runtime *= simulationRatio;
            }

            // Nothing processes runs while no world is loaded.
            if (!LimiterCore.GameOnline) return;
            TrackingManager.UpdateTrackingData(__instance, runtime, collected);

            if (Config.ShowTimingsBurp)
                Log.Info($"[{LimiterCore.GetThreadName()} THREAD] Tracked PB[{__instance.ReadName()}] Runtime in: {debugStamp.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N6", CultureInfo.InvariantCulture)}ms");
        }

        // On its own so the lambda's capture does not allocate on every run. Rarely reached, the script is already dead.
        private static void TurnOffFaultedBlock(MyProgrammableBlock block, MyProgrammableBlock.ScriptTerminationReason reason)
        {
            MySandboxGame.Static.Invoke(() =>
            {
                block.Enabled = false;
                TrackingManager.MarkStopped(block);
            }, "Advanced PB Limiter");

            if (Config.DebugReporting)
                Log.Info($"{block.ReadName()} Script Termination Reason: {reason}");
        }

        /// <summary>Before MyProgrammableBlock.Compile. Returns the value to hand to AfterRecompile.</summary>
        public static RecompileStamp BeforeRecompile(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("PB is null!");
                return default;
            }

            if (!LimiterCore.Active) return new RecompileStamp(false);
            TrackingManager.PBRecompiled(__instance);
            return new RecompileStamp(!__instance.Enabled && !Config.AllowSelfTurnOnExploit);
        }

        /// <summary>After MyProgrammableBlock.Compile, only when this plugin's prefix stamped it.</summary>
        public static void AfterRecompile(MyProgrammableBlock? __instance, RecompileStamp stamp)
        {
            if (__instance is null || !stamp.Started) return;
            if (stamp.KeepDisabled) __instance.Enabled = false;
            TrackingManager.PBCompiled(__instance);
        }

        /// <summary>
        /// Before MyProgrammableBlock.UpdateStorage. Returns false to skip it, in which case the Storage copy it would make
        /// is made here, so only the script's Save() is left out.
        /// </summary>
        public static bool BeforeSave(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("Null programmable block instance using the Save() method. If this happens a lot, let SentorX know please.");
                return true;
            }

            if (__instance.Enabled) return true;
            if (!LimiterCore.Active) return true;

            bool isNpc = MySession.Static.Players.IdentityIsNpc(__instance.OwnerId);
            if (!isNpc && !Config.AllowSelfTurnOnExploit)
            {
                if (Config.DebugReporting)
                {
                    MyIdentity? player = MySession.Static.Players.TryGetIdentity(__instance.OwnerId);
                    if (player is not null)
                        Log.Warn($"{player.DisplayName.SafeName()} ({__instance.OwnerId.ToString(CultureInfo.InvariantCulture)}) tried to save a programmable block [{__instance.ReadName()}] while it was turned off.  This has been disabled.");
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
