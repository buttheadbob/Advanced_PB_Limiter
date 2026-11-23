using Advanced_PB_Limiter.Patches;
using Advanced_PB_Limiter.Utils;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI.Ingame;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedType.Global

namespace Advanced_PB_Limiter.MagnetarHost.Patches;

// Harmony patches on MyProgrammableBlock (Magnetar). The Torch build patches the same methods with Torch's own
// patch manager, and what happens around a run lives in the shared PbProfiler.
// Harmony binds the parameters by name: they must match the original's names.

/// <summary>Runs well before RunSandboxedProgramActionCore, so this is where a run is deferred.</summary>
[HarmonyPatch(typeof(MyProgrammableBlock), "ExecuteCode")]
internal static class ExecuteCodePatch
{
    [HarmonyPrefix]
    private static bool Prefix(MyProgrammableBlock __instance, ref UpdateType updateSource, ref string response)
    {
        return PbProfiler.BeforeExecuteCode(__instance, ref updateSource, ref response);
    }
}

[HarmonyPatch(typeof(MyProgrammableBlock), "RunSandboxedProgramActionCore")]
internal static class RunPatch
{
    // __state is this run's own: a script running another through TryRun nests a second run with its own.
    [HarmonyPrefix]
    private static bool Prefix(MyProgrammableBlock __instance, out RunStamp __state)
    {
        return PbProfiler.BeforeRun(__instance, out __state);
    }

    [HarmonyPostfix]
    private static void Postfix(ref string response, MyProgrammableBlock __instance, ref MyProgrammableBlock.ScriptTerminationReason __result, RunStamp __state)
    {
        PbProfiler.AfterRun(ref response, __instance, ref __result, in __state);
    }
}

[HarmonyPatch(typeof(MyProgrammableBlock), "Compile")]
internal static class CompilePatch
{
    [HarmonyPrefix]
    private static void Prefix(MyProgrammableBlock __instance, out PbProfiler.RecompileStamp __state)
    {
        __state = PbProfiler.BeforeRecompile(__instance);
    }

    [HarmonyPostfix]
    private static void Postfix(MyProgrammableBlock __instance, PbProfiler.RecompileStamp __state)
    {
        PbProfiler.AfterRecompile(__instance, __state);
    }
}

[HarmonyPatch(typeof(MyProgrammableBlock), "UpdateStorage")]
internal static class UpdateStoragePatch
{
    [HarmonyPrefix]
    private static bool Prefix(MyProgrammableBlock __instance)
    {
        return PbProfiler.BeforeSave(__instance);
    }
}
