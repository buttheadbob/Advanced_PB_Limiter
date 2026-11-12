using System;
using System.Reflection;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI.Ingame;
using Torch.Managers.PatchManager;
using Torch.Utils;

// ReSharper disable UnusedMember.Global
// ReSharper disable IdentifierTypo
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Advanced_PB_Limiter.Patches
{
    /// <summary>
    /// Torch native patches (no Harmony). The Magnetar build patches the same methods with Harmony.
    /// What happens around a run lives in the shared PbProfiler.
    /// Torch binds parameters to the original's by NAME only, there is no type check, so each one must be named
    /// exactly like the original's and have its exact type. Names starting __local are locals shared by the patches.
    /// </summary>
    public static class ProfilerPatch_Torch
    {
        private static readonly Logger Log = LogManager.GetLogger("Advanced PB Limiter => Runtime Processor");

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "RunSandboxedProgramActionCore")]
        private static readonly MethodInfo? ProgrammableRun;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "ExecuteCode")]
        private static readonly MethodInfo? ExecuteCode;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "Compile")]
        private static readonly MethodInfo? ProgrammableRecompile;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "UpdateStorage")]
        private static readonly MethodInfo? ProgrammableSaveMethod;

        public static void Patch(PatchContext ctx)
        {
            ReflectedManager.Process(typeof(ProfilerPatch_Torch));

            ctx.GetPattern(ProgrammableRun).Prefixes.Add(PatchMethod(nameof(PrefixProfilePb)));
            ctx.GetPattern(ProgrammableRun).Suffixes.Add(PatchMethod(nameof(SuffixProfilePb)));

            // ExecuteCode is called way before RunSandboxedProgramActionCore so we will use this place to determine defer actions.
            ctx.GetPattern(ExecuteCode).Prefixes.Add(PatchMethod(nameof(PrefixExecuteCode)));

            ctx.GetPattern(ProgrammableRecompile).Prefixes.Add(PatchMethod(nameof(PrefixRecompilePb)));
            ctx.GetPattern(ProgrammableRecompile).Suffixes.Add(PatchMethod(nameof(SuffixRecompilePb)));

            ctx.GetPattern(ProgrammableSaveMethod).Prefixes.Add(PatchMethod(nameof(PrefixSaveMethod)));

            Log.Info("Finished Patching!");
        }

        /// <summary>One of this class's own patch methods, by name. Throws if it is missing, so a renamed method fails at load.</summary>
        private static MethodInfo PatchMethod(string name)
        {
            return typeof(ProfilerPatch_Torch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(ProfilerPatch_Torch).FullName, name);
        }

        private static bool PrefixExecuteCode(MyProgrammableBlock? __instance, ref UpdateType updateSource, ref string response)
        {
            return PbProfiler.BeforeExecuteCode(__instance, ref updateSource, ref response);
        }

        private static bool PrefixProfilePb(MyProgrammableBlock? __instance)
        {
            return PbProfiler.BeforeRun(__instance);
        }

        private static void SuffixProfilePb(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            PbProfiler.AfterRun(ref response, __instance, ref __result);
        }

        private static void PrefixRecompilePb(MyProgrammableBlock? __instance, ref bool __localDisable)
        {
            __localDisable = PbProfiler.BeforeRecompile(__instance);
        }

        private static void SuffixRecompilePb(MyProgrammableBlock? __instance, ref bool __localDisable)
        {
            PbProfiler.AfterRecompile(__instance, __localDisable);
        }

        private static bool PrefixSaveMethod(MyProgrammableBlock? __instance)
        {
            return PbProfiler.BeforeSave(__instance);
        }
    }
}
