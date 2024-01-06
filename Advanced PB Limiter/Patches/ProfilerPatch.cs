using System.Diagnostics;
using System.Reflection;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using Torch.Utils;
using Torch.Utils.Reflected;
// ReSharper disable IdentifierTypo
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Advanced_PB_Limiter.Patches
{
    [ReflectedLazy]
    public static class ProfilerPatch
    {
        /// <summary>
        /// Gracefully stolen (and modified by me) from the original PB Limiter, Credits to HaE, SirHamsterAlot, and Equinox
        /// </summary>
        
        private static readonly Logger Log = LogManager.GetLogger("Advanced PB Limiter Profile Patcher");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        
        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "ExecuteCode")]
        private static readonly MethodInfo? _programmableRunSandboxed;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "Compile")]
        private static readonly MethodInfo? _programmableRecompile;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "UpdateStorage")]
        private static readonly MethodInfo? _programmableSaveMethod;
        
        public static void Patch(PatchContext ctx)
        {
            ReflectedManager.Process(typeof(ProfilerPatch));

            ctx.GetPattern(_programmableRunSandboxed).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch), nameof(PrefixProfilePb)));
            ctx.GetPattern(_programmableRunSandboxed).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch), nameof(SuffixProfilePb)));
            ctx.GetPattern(_programmableRecompile).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch), nameof(PrefixRecompilePb)));
            ctx.GetPattern(_programmableSaveMethod).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch), nameof(PrefixSaveMethod)));

            Log.Info("Finished Patching!");
        }
        
        // ReSharper disable once RedundantAssignment
        private static void PrefixProfilePb(MyProgrammableBlock __instance, ref long __localTimingStart)
        {
            __localTimingStart = Stopwatch.GetTimestamp();
        }
        
        private static void SuffixProfilePb(MyProgrammableBlock? __instance, ref long __localTimingStart)
        {
            if (__instance is null) return;
            TrackingManager.UpdateTrackingData(__instance, (Stopwatch.GetTimestamp() - __localTimingStart) * 1000.0 / Stopwatch.Frequency); 
        }

        private static void PrefixRecompilePb(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("PB is null!");
                return;
            }
            
            TrackingManager.PBRecompiled(__instance);
        }
        
        private static bool PrefixSaveMethod(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("PB is null!");
                return false;
            }

            if (__instance.Enabled) return false;
            
            if (!MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && Config.AllowSelfTurnOnExploit) return true;
            if (MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && !Config.AllowNPCToAutoTurnOn) return true;

            return false;
        }
    }
}