using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using Torch.Utils;
using Torch.Utils.Reflected;
using static Advanced_PB_Limiter.Utils.HelperUtils;

// ReSharper disable IdentifierTypo
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Advanced_PB_Limiter.Patches
{
    [ReflectedLazy]
    public static class ProfilerPatch_Torch
    {
        /// <summary>
        /// Gracefully stolen (and modified by me) from the original PB Limiter, Credits to SirHamsterAlot, and Equinox
        /// </summary>
        private static ConcurrentDictionary<MyProgrammableBlock, string> _pbQueue = new();
        
        // There is overhead to setting up the pb and running its code that is tracked by this and not by the in-game profiler. 
        // Were punishing shitty scripts, nothing more.
        private const double overhead = 0.0;
        
        private static readonly Logger Log = LogManager.GetLogger("Advanced PB Limiter Profile Patcher");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        
        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "ExecuteCode")]
        private static readonly MethodInfo? _programmableRunSandboxed;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "Compile")]
        private static readonly MethodInfo? _programmableRecompile;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "UpdateStorage")]
        private static readonly MethodInfo? _programmableSaveMethod;
        
        private static int gc0 = GC.CollectionCount(0);
        private static int gc1 = GC.CollectionCount(1);
        private static int gc2 = GC.CollectionCount(2);
        private static long beforeMemory = 0;
        
        public static void Patch(PatchContext ctx)
        {
            ReflectedManager.Process(typeof(ProfilerPatch_Torch));

            ctx.GetPattern(_programmableRunSandboxed).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixProfilePb)));
            ctx.GetPattern(_programmableRunSandboxed).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixProfilePb)));
            ctx.GetPattern(_programmableRecompile).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixRecompilePb)));
            ctx.GetPattern(_programmableSaveMethod).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixSaveMethod)));

            Log.Info("Finished Patching!");
        }

        // ReSharper disable once RedundantAssignment
        private static void PrefixProfilePb(ref long __localTimingStart)
        {
            gc0 = GC.CollectionCount(0);
            gc1 = GC.CollectionCount(1);
            gc2 = GC.CollectionCount(2);

            beforeMemory = Process.GetCurrentProcess().WorkingSet64;
            __localTimingStart = Stopwatch.GetTimestamp();
        }
        
        private static void SuffixProfilePb(MyProgrammableBlock? __instance, ref long __localTimingStart)
        {
            long __localTimingEnd = Stopwatch.GetTimestamp();
            TimeSpan measuredSpan = __localTimingEnd.TimeElapsed(__localTimingStart); 
            if (__instance is null) return;
            
            if (gc0 != GC.CollectionCount(0) || gc1 != GC.CollectionCount(1) || gc2 != GC.CollectionCount(2))
               return;

            double runTime = Config.UseSimTime
                ? measuredSpan.TotalMilliseconds / (Sync.ServerSimulationRatio >= 1 ? 1 : Sync.ServerSimulationRatio) - overhead
                : measuredSpan.TotalMilliseconds - overhead;
            
            if (runTime < 0.0d)
                runTime = 0.0d;
            
            TrackingManager.UpdateTrackingData(__instance, runTime, Process.GetCurrentProcess().WorkingSet64 - beforeMemory);
            
            if (Config.DebugReporting)
                Advanced_PB_Limiter.Log.Error($"{__instance.CustomName} : {measuredSpan.TotalMilliseconds / (Sync.ServerSimulationRatio > 1 ? 1 : Sync.ServerSimulationRatio) - overhead} :: {measuredSpan.TotalMilliseconds / (Sync.ServerSimulationRatio > 1 ? 1 : Sync.ServerSimulationRatio) - overhead}");
        }

        private static void PrefixRecompilePb(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("PB is null!");
            }
            
            TrackingManager.PBRecompiled(__instance);
        }
        
        private static bool SuffixSaveMethod(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("PB is null!");
                return false;
            }

            if (__instance.Enabled) return false;
            
            if (!MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && Config.AllowSelfTurnOnExploit) return true;
            if (MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && Config.AllowNPCToAutoTurnOn) return true;

            MyIdentity? player = MySession.Static.Players.TryGetIdentity(__instance.OwnerId);
         
            if (Config.DebugReporting && player is not null)
                Log.Warn($"{player.DisplayName} ({__instance.OwnerId}) tried to save a programmable block while it was turned off.  This is not allowed.");
            return false;
        }
    }
}