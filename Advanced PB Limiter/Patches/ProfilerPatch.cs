using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Metadata;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using Torch.Managers.PatchManager;
using Torch.Utils;
using Torch.Utils.Reflected;
using VRage;
using VRage.Library.Net;
using VRage.Sync;
using static Advanced_PB_Limiter.Utils.HelperUtils;
// ReSharper disable UnusedMember.Global
#pragma warning disable CS0169 // Field is never used

// ReSharper disable IdentifierTypo
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Advanced_PB_Limiter.Patches
{
    public static class ProfilerPatch_Torch
    {
        /// <summary>
        /// Gracefully stolen (and heavily modified by me) from the original PB Limiter, Credits to SirHamsterAlot, and Equinox
        /// </summary>
        private static readonly ConcurrentDictionary<long,MyProgrammableBlock?> DisabledProgrammableBlocks = new();
        
        private static readonly Logger Log = LogManager.GetLogger("Advanced PB Limiter => Runtime Processor");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        private static bool _serverSaved = false;
        private static readonly Timer CheckforSaveTimer = new(16);
        private static long _lastCheckTime = 0;
        private static readonly Dictionary<long, ProfileData> ProfileData = new();
        
        // Dict<EntityID, <Dict<UpdateType,Count>>
        private static readonly Dictionary<long, Dictionary<byte, int>> DeferStatus  = new();
        
        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "RunSandboxedProgramActionCore")]
        private static readonly MethodInfo? ProgrammableRun;
        
        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "ExecuteCode")]
        private static readonly MethodInfo? ExecuteCode;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "Compile")]
        private static readonly MethodInfo? ProgrammableRecompile;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "UpdateStorage")]
        private static readonly MethodInfo? ProgrammableSaveMethod;
        
        [ReflectedGetter(Name = "m_currentAssembly")]
        private static readonly Func<MyProgrammableBlock, Assembly>? GetAssembly;
        
        [ReflectedGetter(Name = "m_instance")]
        private static readonly Func<MyProgrammableBlock, IMyGridProgram>? GetAssemblyInstance;
        
        [ReflectedGetter(Name = "m_runtime")]
        private static readonly Func<MyProgrammableBlock, IMyGridProgramRuntimeInfo>? GetRuntime;
        
        /*
         *  This is a test section to path the player script Main() directly
         */
        
        [ReflectedMethod(Name = "Main")]
        private static readonly Action<IMyGridProgram, string, UpdateType>? CallMain;
        
        private static int _gen0CollectionCount = 0;
        private static int _gen1CollectionCount = 0;
        private static int _gen2CollectionCount = 0;
        
        public static async void Init()
        {
            CheckforSaveTimer.Elapsed += CheckforSaveTimerOnElapsed;
            CheckforSaveTimer.Start();
            
            if (Config.AllowSelfTurnOnExploit)
                return;
            
            if (Config.RequireRecompileOnRestart)
                return;
            
            // I delay this to give the freshly started server time to stabilize.
            if (Config.DebugReporting)
                Log.Warn("Shutting down all pb's that were off during startup in 30 seconds...");
            
            await Task.Delay(30000);
            
            if (Config.DebugReporting)
                Log.Info("Shutting down all pb's that should not be running....");

            _ = Task.Run( async () =>
            {
                await Task.Delay(10000).ConfigureAwait(false);
                ShutdownPb();
            });
        }

        private static void ShutdownPb()
        {
            MySandboxGame.Static.Invoke(() =>
            {
                foreach (KeyValuePair<long, MyProgrammableBlock?> kvp in DisabledProgrammableBlocks)
                {
                    if (kvp.Value == null) continue;
                    
                    if (Config.DebugReporting)
                        Log.Warn("Shutting down: " + kvp.Value.CustomName);
                    
                    kvp.Value.Enabled = false;
                    DisabledProgrammableBlocks.TryRemove(kvp.Key, out _);
                }
            },"PB Limiter");
        }

        private static void CheckforSaveTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (MySession.Static.ServerSaving)
            {
                _lastCheckTime = Stopwatch.GetTimestamp();
                _serverSaved = true;
                return;
            }

            if (_lastCheckTime.TicksTillNow_TimeSpan().TotalSeconds > 10)
                _serverSaved = false;
        }

        public static void Patch(PatchContext ctx)
        {
            ReflectedManager.Process(typeof(ProfilerPatch_Torch));
            
            ctx.GetPattern(ProgrammableRun).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixProfilePb)));
            ctx.GetPattern(ProgrammableRun).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixProfilePb)));
            
            // ExecuteCode is called way before RunSandboxedProgramActionCore so we will use this place to determine defer actions. 
            ctx.GetPattern(ExecuteCode).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixExecuteCode)));
            
            ctx.GetPattern(ProgrammableRecompile).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixRecompilePb)));
            ctx.GetPattern(ProgrammableRecompile).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixRecompilePb)));
            
            ctx.GetPattern(ProgrammableSaveMethod).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixSaveMethod)));

            if (Config.DebugReporting)
                Log.Info("Finished Patching!");
        }
        
        // ReSharper disable once RedundantAssignment
        private static bool PrefixExecuteCode(MyProgrammableBlock? __instance, ref UpdateType updateSource, ref string response)
        {
            if (!Config.Enabled) return true;
            if (__instance == null) return true;
            response = string.Empty;
            _gen0CollectionCount = GC.CollectionCount(0);
            _gen1CollectionCount = GC.CollectionCount(1);
            _gen2CollectionCount = GC.CollectionCount(2);
            
            long time = Stopwatch.GetTimestamp();
            if ((Config.DeferRunOnSimspeedRate && Sync.ServerSimulationRatio < Config.DeferRunBelowSimRate) || Config.DeferRunAlways  || Config.DisableUpdate1)
            {
                if (!DeferStatus.TryGetValue(__instance.EntityId, out Dictionary<byte, int>? blockDeferStatus) || blockDeferStatus == null)
                {
                    blockDeferStatus = new Dictionary<byte, int>
                    {
                        {1,0},{10,0},{100,0}
                    };
                    DeferStatus.Add(__instance.EntityId, blockDeferStatus);
                    
                    // This would only not exist on first run, which is the instantiation run and should go without intervention.
                    return true;
                }

                bool allow = false;
                if ((updateSource & UpdateType.Update1) == UpdateType.Update1)
                {
                    blockDeferStatus[1] ++;
                    if (Config.DisableUpdate1)
                    {
                        updateSource |= UpdateType.Update10;
                    }
                    else
                    {
                        if (blockDeferStatus[1] >= Config.DeferRunCount)
                        {
                            allow = true;
                            blockDeferStatus[1] = 0;
                        }
                    }
                }

                if ((updateSource & UpdateType.Update10) == UpdateType.Update10)
                {
                    blockDeferStatus[10]++;
                    if (blockDeferStatus[10] >= Config.DeferRunCount)
                    {
                        allow = true;
                        blockDeferStatus[10] = 0;
                    }
                }

                if ((updateSource & UpdateType.Update100) == UpdateType.Update100)
                {
                    blockDeferStatus[100]++;
                    if (blockDeferStatus[100] >= Config.DeferRunCount)
                    {
                        allow = true;
                        blockDeferStatus[100] = 0;
                    }
                }

                if (!allow)
                {
                    if (Config.ShowTimingsBurp)
                        Log.Info($"Defer Manager took {time.TicksTillNow_TimeSpan().TotalMilliseconds:N5}ms for {__instance.CustomName}.");
                    
                    return false;
                }
            }
                
            if (Config.ShowTimingsBurp)
                Log.Info($"Defer Manager took {time.TicksTillNow_TimeSpan().TotalMilliseconds:N5}ms for {__instance.CustomName}.");
            
            return true;
        }
        
        private static bool PrefixProfilePb(MyProgrammableBlock? __instance)
        {
            if (!Config.Enabled) return true;
            
            if (__instance == null)
                return true; // Something funky is going on so we won't track it this pass but don't block it either.
            
            if (!ProfileData.ContainsKey(__instance.EntityId))
                ProfileData.Add(__instance.EntityId, new ProfileData());
            
            ProfileData[__instance.EntityId].TimingStart = 0;

            if (!__instance.Enabled)
            {
                if (!Config.AllowSelfTurnOnExploit)
                {
                    if (Config.RequireRecompileOnRestart)
                    {
                        if (Config.DebugReporting)
                            Log.Warn("Skipping recompile for pb: " + __instance.CustomName);

                        ProfileData[__instance.EntityId].Skipped = true;
                        return false;
                    }
                    
                    if (DisabledProgrammableBlocks.TryAdd(__instance.EntityId, __instance))
                    {
                        if (Config.DebugReporting)
                            Log.Warn($"Programmable Block {__instance.CustomName} was not enabled during restart and will be disabled again shortly.");
                    }
                }
            }

            if (TrackingManager.IsMemoryCheckInProgress(__instance.EntityId))
            {
                if (Config.DebugReporting)
                    Log.Warn($"Skipping programmable block[{__instance.CustomName}] run for memory check.");
                
                ProfileData[__instance.EntityId].Skipped = true;
                return false;
            }

            ProfileData[__instance.EntityId].TimingStart = Stopwatch.GetTimestamp();
            return true;
        }
        
        private static void SuffixProfilePb(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            if (!Config.Enabled) return;
            if (__instance == null) return; // Something funky is going on, so we won't track it this pass but don't block it either.
            
            double runtime = !Config.UseGameReportedRuntime 
                ? Stopwatch.GetTimestamp().TimeElapsed(ProfileData[__instance.EntityId].TimingStart).TotalMilliseconds  
                : GetRuntime!(__instance).LastRunTimeMs;
            
            if (Config is { UseGameReportedRuntime: false, DebugReporting: true })
            {
                double gameRuntime = GetRuntime!(__instance).LastRunTimeMs;
                Log.Debug($"PB[{__instance.CustomName}] Stopwatch: {runtime:0.0000}ms  GameReported: {gameRuntime:0.0000}ms  Delta: {runtime - gameRuntime:0.0000}ms");
            }
            
            long debugStamp = Stopwatch.GetTimestamp();
            
            if (ProfileData[__instance.EntityId].Skipped)
            {
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
                ProfileData[__instance.EntityId].Skipped = false;
                return;
            }
            
            // GC stuff
            if (_gen0CollectionCount != GC.CollectionCount(0))
            {
                if (Config.DebugReporting) Log.Warn("GC Gen 1 Occured.  Not reporting runtime.");
                return;
            }
            if (_gen1CollectionCount != GC.CollectionCount(1))
            {
                if (Config.DebugReporting) Log.Warn("GC Gen 2 Occured.  Not reporting runtime.");
                return;
            }
            if (_gen2CollectionCount != GC.CollectionCount(2))
            {
                if (Config.DebugReporting) Log.Warn("GC Gen 3 Occured.  Not reporting runtime.");
                return;
            }
            
            if (!ProfileData.ContainsKey(__instance.EntityId) || ProfileData[__instance.EntityId] == null)
                if (Config.DebugReporting)
                {
                    Log.Warn("Profile data for PB is missing!");
                    return;
                }

            if (string.IsNullOrEmpty(response))
                response = "";
            
            if (!Enum.TryParse(__result.ToString(), true, out MyProgrammableBlock.ScriptTerminationReason scriptTerminationReason))
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
            
            if (ProfileData[__instance.EntityId].TimingStart == 0)
                return;
            
            // the pb will still run up to the point of executing the script even when disabled by script termination fault, this will stop some of that.
            // also stops the limiter from checking the block.
            if (__result != MyProgrammableBlock.ScriptTerminationReason.None)
            {
                MySandboxGame.Static.Invoke(() => { __instance.Enabled = false;}, "Advanced PB Limiter");
                TrackingManager.RemovePbInstanceReference(__instance.EntityId);
                if (Config.DebugReporting)
                {
                    Advanced_PB_Limiter.Log.Info($"{__instance.CustomName} : {runtime / (Sync.ServerSimulationRatio > 1 ? 1 : Sync.ServerSimulationRatio)} :: {runtime / (Sync.ServerSimulationRatio > 1 ? 1 : Sync.ServerSimulationRatio)}");
                    Log.Info("Script Termination Reason:" + __result);
                }

                return;
            }

            if (Config.UseSimTime)
                runtime /= Sync.ServerSimulationRatio >= 1 ? 1 : Sync.ServerSimulationRatio;
            
            if (GetAssemblyInstance!(__instance) != null)
                if (!TrackingManager.IsPbInstanceReferenceValid(__instance.EntityId))
                    TrackingManager.AddPbInstanceReference(__instance.EntityId, new ProgramInfo{ ProgramReference = new WeakReference<IMyGridProgram>(GetAssemblyInstance(__instance)), LastUpdate = DateTime.Now, OwnerId = __instance.OwnerId, Pb = __instance} );
            
            if (!_serverSaved)
                TrackingManager.UpdateTrackingData(__instance, runtime); 
            
            if (Config.ShowTimingsBurp)
                Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Tracked PB[{__instance.CustomName}] Runtime in: {debugStamp.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N6", CultureInfo.InvariantCulture)}ms");
        }

        private static void PrefixRecompilePb(MyProgrammableBlock? __instance, ref bool __localDisable)
        {
            if (__instance is null)
            {
                Log.Warn("PB is null!");
                return;
            }

            if (__instance.Enabled == false && !Config.AllowSelfTurnOnExploit)
            {
                __instance.Enabled = false;
                __localDisable = true;
            }

            TrackingManager.RemovePbInstanceReference(__instance.EntityId);
            
            TrackingManager.PBRecompiled(__instance);
        }

        private static void SuffixRecompilePb(MyProgrammableBlock? __instance, ref bool __localDisable)
        {
            if (__instance is null) return;
            if (__localDisable) __instance.Enabled = false;
        }
        
        private static bool PrefixSaveMethod(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("Null programmable block instance using the Save() method.. interesting.  If this happens a lot, let SentorX know please.");
                return true;
            }

            if (__instance.Enabled) return true;

            if (!MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && !Config.AllowSelfTurnOnExploit)
            {
                MyIdentity? player = MySession.Static.Players.TryGetIdentity(__instance.OwnerId);
                if (Config.DebugReporting && player is not null)
                    Log.Warn($"{player.DisplayName} ({__instance.OwnerId}) tried to save a programmable block [{__instance.CustomName}] while it was turned off.  This is has been disabled.");
                return false;
            }
            
            if (MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && !Config.AllowNPCToAutoTurnOn) 
                return false;
            
            return true;
        }
    }
}