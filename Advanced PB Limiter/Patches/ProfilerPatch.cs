using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Metadata;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Torch.Managers.PatchManager;
using Torch.Utils;
using Torch.Utils.Reflected;
using VRage;
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
        
        // There is overhead to setting up the pb and running its code that is tracked by this and not by the in-game profiler. 
        // We're punishing shitty scripts, nothing more.
        private const double overhead = 0.00;
        
        private static readonly Logger Log = LogManager.GetLogger("Advanced PB Limiter Profile Patcher");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        private const string termToRemove = "Me.Enabled";
        private static string pattern = $@"\s*{Regex.Escape(termToRemove)}\s*=\s*True\s*";
        
        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "RunSandboxedProgramActionCore")]
        private static readonly MethodInfo? _programmableRun;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "Compile")]
        private static readonly MethodInfo? _programmableRecompile;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "UpdateStorage")]
        private static readonly MethodInfo? _programmableSaveMethod;
        
        [ReflectedGetter(Name = "m_assembly")]
        private static readonly Func<MyProgrammableBlock, Assembly> get_Assembly;
        
        [ReflectedGetter(Name = "m_instance")]
        private static readonly Func<MyProgrammableBlock, IMyGridProgram> get_AssemblyInstance;
        
        
        
        
        public static void Patch(PatchContext ctx)
        {
            ReflectedManager.Process(typeof(ProfilerPatch_Torch));
            
            ctx.GetPattern(_programmableRun).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixProfilePb)));
            ctx.GetPattern(_programmableRun).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixProfilePb)));
            
            ctx.GetPattern(_programmableRecompile).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixRecompilePb)));
            ctx.GetPattern(_programmableSaveMethod).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixSaveMethod)));

            Log.Info("Finished Patching!");
        }

        // ReSharper disable once RedundantAssignment
        private static bool PrefixProfilePb(MyProgrammableBlock? __instance, ref long __localTimingStart, ref bool __localSkipped)
        {
            __localTimingStart = 0;
            if (__instance == null)
            {
                __localSkipped = true;
                return false;
            }

            if (__instance is { Enabled: false } && !Config.AllowSelfTurnOnExploit)
            {
                __localSkipped = true;
                return false;
            }

            if (TrackingManager.IsMemoryCheckInProgress(__instance.EntityId))
            {
                __localSkipped = true;
                return false;
            }

            __localTimingStart = Stopwatch.GetTimestamp();
            return true;
        }
        
        private static void SuffixProfilePb(ref string response, MyProgrammableBlock? __instance, ref long __localTimingStart, ref bool __localSkipped, ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            TimeSpan measuredSpan = Stopwatch.GetTimestamp().TimeElapsed(__localTimingStart);
            
            if (__localSkipped)
            {
                __result = MyProgrammableBlock.ScriptTerminationReason.AlreadyRunning;
                response = "";
                return;
            }
            
            if (__instance == null) return;
            if (__localTimingStart == 0) return;
            
            // the pb will still run up to the point of executing the script even when disabled by script termination fault, this will stop some of that.
            // also stops the limiter from checking the block.
            if (__result != MyProgrammableBlock.ScriptTerminationReason.None)
            {
                if (__result != MyProgrammableBlock.ScriptTerminationReason.AlreadyRunning)
                {
                    __instance.Enabled = false;
                    TrackingManager.RemovePBInstanceReference(__instance.EntityId);
                    if (Config.DebugReporting)
                    {
                        Advanced_PB_Limiter.Log.Info($"{__instance.CustomName} : {measuredSpan.TotalMilliseconds / (Sync.ServerSimulationRatio > 1 ? 1 : Sync.ServerSimulationRatio) - overhead} :: {measuredSpan.TotalMilliseconds / (Sync.ServerSimulationRatio > 1 ? 1 : Sync.ServerSimulationRatio) - overhead}");
                        Log.Info("Script Termination Reason:" + __result);
                    }

                    return;
                }
            }
            
            double runTime = Config.UseSimTime
                ? measuredSpan.TotalMilliseconds / (Sync.ServerSimulationRatio >= 1 ? 1 : Sync.ServerSimulationRatio) - overhead
                : measuredSpan.TotalMilliseconds - overhead;
            
            if (get_AssemblyInstance(__instance) != null)
            {
                if (!TrackingManager.IsPBInstanceReferenceValid(__instance.EntityId))
                {
                    TrackingManager.AddPBInstanceReference(__instance.EntityId, new ProgramInfo{ ProgramReference = new WeakReference<IMyGridProgram>(get_AssemblyInstance(__instance)), LastUpdate = DateTime.Now, OwnerID = __instance.OwnerId} );
                    return;
                }
            }
            
            TrackingManager.UpdateTrackingData(__instance, runTime); 
        }

        private static void PrefixRecompilePb(MyProgrammableBlock? __instance, ref string program)
        {
            if (__instance is null)
            {
                Log.Warn("PB is null!");
                return;
            }

            TrackingManager.RemovePBInstanceReference(__instance.EntityId);
            
            if (!Config.AllowSelfTurnOnExploit)
                program = Regex.Replace(program, pattern, string.Empty, RegexOptions.IgnoreCase);
            
            TrackingManager.PBRecompiled(__instance);
        }
        
        private static bool PrefixSaveMethod(MyProgrammableBlock? __instance)
        {
            if (__instance is null)
            {
                Log.Warn("PB is null!");
                return false;
            }

            if (__instance.Enabled) return true;

            if (!MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && !Config.AllowSelfTurnOnExploit)
            {
                MyIdentity? player = MySession.Static.Players.TryGetIdentity(__instance.OwnerId);
                if (Config.DebugReporting && player is not null)
                    Log.Warn($"{player.DisplayName} ({__instance.OwnerId}) tried to save a programmable block while it was turned off.  This is not allowed.");
                return false;
            }
            
            if (MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && !Config.AllowNPCToAutoTurnOn) 
                return false;
            
            return true;
        }
    }

    
}