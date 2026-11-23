using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Advanced_PB_Limiter.Utils;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI.Ingame;
using Torch.Managers.PatchManager;

// ReSharper disable UnusedMember.Global
// ReSharper disable IdentifierTypo

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
        private static readonly TorchLog Log = new("Advanced PB Limiter => Runtime Processor");

        /// <summary>Finds every target first, so a game change throws before anything is patched. Torch applies the patches after Init.</summary>
        public static void Patch(PatchManager patchManager)
        {
            Type block = typeof(MyProgrammableBlock);
            MethodInfo programmableRun = GameMembers.OnlyMethod(block, "RunSandboxedProgramActionCore");
            MethodInfo executeCode = GameMembers.OnlyMethod(block, "ExecuteCode");
            MethodInfo programmableRecompile = GameMembers.OnlyMethod(block, "Compile");
            MethodInfo programmableSaveMethod = GameMembers.OnlyMethod(block, "UpdateStorage");

            PatchContext ctx = patchManager.AcquireContext();
            try
            {
                AddPatches(ctx, programmableRun, executeCode, programmableRecompile, programmableSaveMethod);
            }
            catch (Exception)
            {
                patchManager.FreeContext(ctx);
                throw;
            }

            Log.Info("Finished Patching!");
        }

        private static void AddPatches(PatchContext ctx, MethodInfo programmableRun, MethodInfo executeCode, MethodInfo programmableRecompile, MethodInfo programmableSaveMethod)
        {
            MethodInfo prefixProfilePb = PatchMethod(nameof(PrefixProfilePb));
            MethodInfo suffixProfilePb = PatchMethod(nameof(SuffixProfilePb));
            MethodInfo prefixExecuteCode = PatchMethod(nameof(PrefixExecuteCode));
            MethodInfo prefixRecompilePb = PatchMethod(nameof(PrefixRecompilePb));
            MethodInfo suffixRecompilePb = PatchMethod(nameof(SuffixRecompilePb));
            MethodInfo prefixSaveMethod = PatchMethod(nameof(PrefixSaveMethod));

            // Torch binds the parameters when it applies the patches, after Init and outside its try: a parameter the game
            // renamed or retyped would stop Torch from starting there, so each target's patches are checked here first.
            CheckBinds(programmableRun, new[] { prefixProfilePb }, new[] { suffixProfilePb });
            CheckBinds(executeCode, new[] { prefixExecuteCode }, Array.Empty<MethodInfo>());
            CheckBinds(programmableRecompile, new[] { prefixRecompilePb }, new[] { suffixRecompilePb });
            CheckBinds(programmableSaveMethod, new[] { prefixSaveMethod }, Array.Empty<MethodInfo>());

            ctx.GetPattern(programmableRun).Prefixes.Add(prefixProfilePb);
            ctx.GetPattern(programmableRun).Suffixes.Add(suffixProfilePb);

            // ExecuteCode is called way before RunSandboxedProgramActionCore so we will use this place to determine defer actions.
            ctx.GetPattern(executeCode).Prefixes.Add(prefixExecuteCode);

            ctx.GetPattern(programmableRecompile).Prefixes.Add(prefixRecompilePb);
            ctx.GetPattern(programmableRecompile).Suffixes.Add(suffixRecompilePb);

            ctx.GetPattern(programmableSaveMethod).Prefixes.Add(prefixSaveMethod);
        }

        private const string LocalPrefix = "__local";

        /// <summary>
        /// Throws unless Torch can apply these prefixes and suffixes to the target, by the rules of Torch's DecoratedMethod
        /// for the parameter names these patches use. Prefixes return void or bool, suffixes void. Each parameter binds by name:
        /// __instance: the target is not static, and the parameter, by value, takes the target's declaring type.
        /// __result: the target returns a value. By value the parameter takes the return type, as Torch requires; by ref it
        /// is the return type itself, as Torch passes the address of its own local of that type.
        /// __local and more: a local the patches share, of one type wherever the name appears among the patches given here.
        /// Any other name without two leading underscores: the target's parameter of that name and type, by value or by ref
        /// either way, as Torch converts.
        /// Any other special name Torch knows (__original, __prefixSkipped, __field_...) is refused: no patch here uses one,
        /// and a new patch that does gets its rule added here. Torch itself loads __instance and parameters without checking
        /// their types, and passes a value type to a parameter of a wider type without boxing it, so those are held to the
        /// types that work. A local of the same name in another plugin's patch of the same target is not seen here.
        /// </summary>
        private static void CheckBinds(MethodInfo target, IReadOnlyList<MethodInfo> prefixes, IReadOnlyList<MethodInfo> suffixes)
        {
            Type declaringType = target.DeclaringType ?? throw new MissingMemberException($"{target.Name} has no declaring type.");
            Dictionary<string, Type> locals = new(StringComparer.Ordinal);
            foreach (MethodInfo prefix in prefixes)
            {
                if (prefix.ReturnType != typeof(void) && prefix.ReturnType != typeof(bool))
                    throw new InvalidOperationException($"{prefix.Name} is a prefix, so it must return void or bool.");
                CheckParameters(target, declaringType, prefix, locals);
            }

            foreach (MethodInfo suffix in suffixes)
            {
                if (suffix.ReturnType != typeof(void))
                    throw new InvalidOperationException($"{suffix.Name} is a suffix, so it must return void.");
                CheckParameters(target, declaringType, suffix, locals);
            }
        }

        private static void CheckParameters(MethodInfo target, Type declaringType, MethodInfo patch, Dictionary<string, Type> locals)
        {
            foreach (ParameterInfo parameter in patch.GetParameters())
            {
                string name = parameter.Name ?? "";
                Type type = parameter.ParameterType;
                Type valueType = type.IsByRef ? type.GetElementType()! : type;
                bool binds = name switch
                {
                    "__instance" => !target.IsStatic && !type.IsByRef && type.IsAssignableFrom(declaringType),
                    "__result" => target.ReturnType != typeof(void) && Takes(type, target.ReturnType),
                    _ when name.StartsWith(LocalPrefix, StringComparison.Ordinal) => SameLocal(locals, name, valueType, target, patch),
                    _ when name.StartsWith("__", StringComparison.Ordinal) => throw new InvalidOperationException($"{patch.Name} takes {name}, a name CheckBinds has no rule for yet; add it before using it."),
                    _ => target.GetParameters().Any(original => string.Equals(original.Name, name, StringComparison.Ordinal)
                                                                && (original.ParameterType.IsByRef ? original.ParameterType.GetElementType() : original.ParameterType) == valueType)
                };
                if (!binds)
                    throw new MissingMemberException($"{declaringType.FullName}.{target.Name} no longer has what {patch.Name} takes as {name} ({valueType.Name}).");
            }
        }

        /// <summary>True when a parameter of this type can take a value of the other type the way Torch passes it.</summary>
        private static bool Takes(Type parameter, Type value)
        {
            if (parameter.IsByRef)
                return parameter.GetElementType() == value;
            // Torch does not box, so a value type goes only to a parameter of its own type.
            return value.IsValueType ? parameter == value : parameter.IsAssignableFrom(value);
        }

        /// <summary>Keeps the first type a local is used with, and throws when a later use has another, as Torch would.</summary>
        private static bool SameLocal(Dictionary<string, Type> locals, string name, Type type, MethodInfo target, MethodInfo patch)
        {
            if (!locals.TryGetValue(name, out Type? first))
            {
                locals.Add(name, type);
                return true;
            }
            if (first != type)
                throw new InvalidOperationException($"{patch.Name} takes {name} as {type.Name}, but an earlier patch of {target.Name} takes it as {first.Name}; Torch refuses a shared local with two types.");
            return true;
        }

        /// <summary>One of this class's own patch methods, by name. Throws if it is missing, so a renamed method fails at load.</summary>
        private static MethodInfo PatchMethod(string name)
        {
            return typeof(ProfilerPatch_Torch).GetTypeInfo().DeclaredMethods.FirstOrDefault(method => method.IsStatic && string.Equals(method.Name, name, StringComparison.Ordinal))
                ?? throw new MissingMethodException(typeof(ProfilerPatch_Torch).FullName, name);
        }

        private static bool PrefixExecuteCode(MyProgrammableBlock? __instance, ref UpdateType updateSource, ref string response)
        {
            return PbProfiler.BeforeExecuteCode(__instance, ref updateSource, ref response);
        }

        // __localRunStamp is this run's own local: a script running another through TryRun nests a second run with its own.
        private static bool PrefixProfilePb(MyProgrammableBlock? __instance, ref RunStamp __localRunStamp)
        {
            return PbProfiler.BeforeRun(__instance, out __localRunStamp);
        }

        private static void SuffixProfilePb(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result, ref RunStamp __localRunStamp)
        {
            PbProfiler.AfterRun(ref response, __instance, ref __result, in __localRunStamp);
        }

        private static void PrefixRecompilePb(MyProgrammableBlock? __instance, ref PbProfiler.RecompileStamp __localDisable)
        {
            __localDisable = PbProfiler.BeforeRecompile(__instance);
        }

        private static void SuffixRecompilePb(MyProgrammableBlock? __instance, ref PbProfiler.RecompileStamp __localDisable)
        {
            PbProfiler.AfterRecompile(__instance, __localDisable);
        }

        private static bool PrefixSaveMethod(MyProgrammableBlock? __instance)
        {
            return PbProfiler.BeforeSave(__instance);
        }
    }
}
