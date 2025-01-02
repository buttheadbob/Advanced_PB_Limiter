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

// ReSharper disable IdentifierTypo
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Advanced_PB_Limiter.Patches
{
    public static class ProfilerPatch_Torch
    {
        /// <summary>
        /// Gracefully stolen (and heavily modified by me) from the original PB Limiter, Credits to SirHamsterAlot, and Equinox
        /// </summary>
        private static readonly ConcurrentDictionary<long,MyProgrammableBlock?> _disabledProgrammableBlocks = new();
        
        private static readonly Logger Log = LogManager.GetLogger("Advanced PB Limiter => Runtime Processor");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        private static bool ServerSaved = false;
        private static readonly Timer checkforSaveTimer = new(16);
        private static long lastCheckTime = 0;
        private static readonly Dictionary<long, ProfileData> _profileData = new();
        
        // Dict<EntityID, <Dict<UpdateType,Count>>
        private static readonly Dictionary<long, Dictionary<byte, int>> deferStatus  = new();
        
        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "RunSandboxedProgramActionCore")]
        private static readonly MethodInfo? _programmableRun;
        
        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "ExecuteCode")]
        private static readonly MethodInfo? _executeCode;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "Compile")]
        private static readonly MethodInfo? _programmableRecompile;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "UpdateStorage")]
        private static readonly MethodInfo? _programmableSaveMethod;
        
        [ReflectedGetter(Name = "m_assembly")]
        private static readonly Func<MyProgrammableBlock, Assembly> get_Assembly;
        
        [ReflectedGetter(Name = "m_instance")]
        private static readonly Func<MyProgrammableBlock, IMyGridProgram> get_AssemblyInstance;
        
        public static async void Init()
        {
            checkforSaveTimer.Elapsed += CheckforSaveTimerOnElapsed;
            checkforSaveTimer.Start();
            
            if (Config.AllowSelfTurnOnExploit)
                return;
            
            if (Config.RequireRecompileOnRestart)
                return;
            
            if (Config.DebugReporting)
                Log.Warn("Shutting down all pb's that were off during startup in 30 seconds...");
            
            await Task.Delay(30000);
            
            if (Config.DebugReporting)
                Log.Info("Shutting down all pb's that should not be running....");

            _ = Task.Run( async () =>
            {
                await Task.Delay(10000).ConfigureAwait(false);
                ShutdownPB();
            });
        }

        private static void ShutdownPB()
        {
            MySandboxGame.Static.Invoke(() =>
            {
                foreach (KeyValuePair<long, MyProgrammableBlock?> kvp in _disabledProgrammableBlocks)
                {
                    if (kvp.Value == null) continue;
                    
                    if (Config.DebugReporting)
                        Log.Warn("Shutting down: " + kvp.Value.CustomName);
                    
                    kvp.Value.Enabled = false;
                    _disabledProgrammableBlocks.TryRemove(kvp.Key, out _);
                }
            },"PB Limiter");
        }

        private static void CheckforSaveTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (MySession.Static.ServerSaving)
            {
                lastCheckTime = Stopwatch.GetTimestamp();
                ServerSaved = true;
                return;
            }

            if (lastCheckTime.TicksTillNow_TimeSpan().TotalSeconds > 10)
                ServerSaved = false;
        }

        public static void Patch(PatchContext ctx)
        {
            ReflectedManager.Process(typeof(ProfilerPatch_Torch));
            
            ctx.GetPattern(_programmableRun).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixProfilePb)));
            ctx.GetPattern(_programmableRun).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixProfilePb)));
            
            // ExecuteCode is called way before RunSandboxedProgramActionCore so we will use this place to determine defer actions. 
            ctx.GetPattern(_executeCode).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixExecuteCode)));
            
            ctx.GetPattern(_programmableRecompile).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixRecompilePb)));
            ctx.GetPattern(_programmableRecompile).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixRecompilePb)));
            
            ctx.GetPattern(_programmableSaveMethod).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixSaveMethod)));

            if (Config.DebugReporting)
                Log.Info("Finished Patching!");
        }

        // ReSharper disable once RedundantAssignment
        private static bool PrefixExecuteCode(MyProgrammableBlock? __instance, ref UpdateType updateSource, ref string response)
        {
            if (!Config.Enabled) return true;
            if (__instance == null) return true;
            long time = Stopwatch.GetTimestamp();
            response = string.Empty;
            if ((Config.DeferRunOnSimspeedRate && Sync.ServerSimulationRatio < Config.DeferRunBelowSimRate) || Config.DeferRunAlways  || Config.DisableUpdate1)
            {
                if (!deferStatus.TryGetValue(__instance.EntityId, out Dictionary<byte, int>? blockDeferStatus) || blockDeferStatus == null)
                {
                    blockDeferStatus = new Dictionary<byte, int>
                    {
                        {1,0},{10,0},{100,0}
                    };
                    deferStatus.Add(__instance.EntityId, blockDeferStatus);
                    
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
            
            if (!_profileData.ContainsKey(__instance.EntityId))
                _profileData.Add(__instance.EntityId, new ProfileData());
            
            _profileData[__instance.EntityId].TimingStart = 0;

            if (!__instance.Enabled)
            {
                if (!Config.AllowSelfTurnOnExploit)
                {
                    if (Config.RequireRecompileOnRestart)
                    {
                        if (Config.DebugReporting)
                            Log.Warn("Skipping recompile for pb: " + __instance.CustomName);

                        _profileData[__instance.EntityId].Skipped = true;
                        return false;
                    }
                    
                    if (_disabledProgrammableBlocks.TryAdd(__instance.EntityId, __instance))
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
                
                _profileData[__instance.EntityId].Skipped = true;
                return false;
            }

            _profileData[__instance.EntityId].TimingStart = Stopwatch.GetTimestamp();
            return true;
        }
        
        private static void SuffixProfilePb(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            if (__instance == null) return; // Something funky is going on so we won't track it this pass but don't block it either.
            
            long debugStamp = Stopwatch.GetTimestamp();
            
            if (!Config.Enabled) return;

            if (!_profileData.ContainsKey(__instance.EntityId) || _profileData[__instance.EntityId] == null)
                if (Config.DebugReporting)
                {
                    Log.Warn("Profile data for PB is missing!");
                    return;
                }
            
            if (string.IsNullOrEmpty(response))
                response = "";

            if (!Enum.TryParse(__result.ToString(), true, out MyProgrammableBlock.ScriptTerminationReason scriptTerminationReason))
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
            
            if (_profileData[__instance.EntityId].Skipped)
            {
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
                return;
            }
            
            if (_profileData[__instance.EntityId].TimingStart == 0)
                return;
            
            TimeSpan measuredSpan = Stopwatch.GetTimestamp().TimeElapsed(_profileData[__instance.EntityId].TimingStart);
            
            // the pb will still run up to the point of executing the script even when disabled by script termination fault, this will stop some of that.
            // also stops the limiter from checking the block.
            if (__result != MyProgrammableBlock.ScriptTerminationReason.None)
            {
                MySandboxGame.Static.Invoke(() => { __instance.Enabled = false;}, "Advanced PB Limiter");
                TrackingManager.RemovePBInstanceReference(__instance.EntityId);
                if (Config.DebugReporting)
                {
                    Advanced_PB_Limiter.Log.Info($"{__instance.CustomName} : {measuredSpan.TotalMilliseconds / (Sync.ServerSimulationRatio > 1 ? 1 : Sync.ServerSimulationRatio)} :: {measuredSpan.TotalMilliseconds / (Sync.ServerSimulationRatio > 1 ? 1 : Sync.ServerSimulationRatio)}");
                    Log.Info("Script Termination Reason:" + __result);
                }

                return;
            }
            
            double runTime = Config.UseSimTime
                ? measuredSpan.TotalMilliseconds / (Sync.ServerSimulationRatio >= 1 ? 1 : Sync.ServerSimulationRatio)
                : measuredSpan.TotalMilliseconds;
            
            if (get_AssemblyInstance(__instance) != null)
                if (!TrackingManager.IsPBInstanceReferenceValid(__instance.EntityId))
                    TrackingManager.AddPBInstanceReference(__instance.EntityId, new ProgramInfo{ ProgramReference = new WeakReference<IMyGridProgram>(get_AssemblyInstance(__instance)), LastUpdate = DateTime.Now, OwnerID = __instance.OwnerId, PB = __instance} );
            
            if (!ServerSaved)
                TrackingManager.UpdateTrackingData(__instance, runTime); 
            
            if (Config.ShowTimingsBurp)
                Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Tracked PB[{__instance.CustomName}] Runtime in: {debugStamp.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
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

            TrackingManager.RemovePBInstanceReference(__instance.EntityId);
            
            //if (!Config.AllowSelfTurnOnExploit)
            //    program = Regex.Replace(program, pattern, string.Empty, RegexOptions.IgnoreCase);
            
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