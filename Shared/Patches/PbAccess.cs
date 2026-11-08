using System;
using System.Linq.Expressions;
using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;

namespace Advanced_PB_Limiter.Patches
{
    /// <summary>The private members of MyProgrammableBlock the limiter reads or calls.</summary>
    internal static class PbAccess
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // Called on every programmable block run, so these are compiled instead of going through reflection.
        public static Func<MyProgrammableBlock, IMyGridProgram?> GetProgram { get; private set; } = null!;
        public static Func<MyProgrammableBlock, IMyGridProgramRuntimeInfo> GetRuntime { get; private set; } = null!;

        // Only used when a block is punished.
        private static FieldInfo _needsInstantiation = null!;
        private static MethodInfo _onProgramTermination = null!;

        // Only used when Save() is skipped on a block that is turned off.
        private static FieldInfo _storageData = null!;

        public static void Init()
        {
            GetProgram = CompileGetter<IMyGridProgram?>("m_instance");
            GetRuntime = CompileGetter<IMyGridProgramRuntimeInfo>("m_runtime");
            _needsInstantiation = GetField("m_needsInstantiation");
            _storageData = GetField("m_storageData");
            _onProgramTermination = typeof(MyProgrammableBlock).GetMethod("OnProgramTermination", InstanceFlags)
                ?? throw new InvalidOperationException("Could not find MyProgrammableBlock.OnProgramTermination, the game has probably changed");
        }

        /// <summary>Stops the block recompiling the script on its own. Do this before OnProgramTermination, or on its own.</summary>
        public static void ClearNeedsInstantiation(MyProgrammableBlock block)
        {
            _needsInstantiation.SetValue(block, false);
        }

        /// <summary>
        /// The first step of MyProgrammableBlock.UpdateStorage, without the Save() call: copies the script's Storage to the block.
        /// Recompile passes that copy to the new script, so without it a recompile goes back to an older Storage. Runs no script code.
        /// </summary>
        public static void CopyStorage(MyProgrammableBlock block)
        {
            IMyGridProgram? program = GetProgram(block);
            if (program is not null)
                _storageData.SetValue(block, program.Storage);
        }

        public static void TerminateProgram(MyProgrammableBlock block)
        {
            _onProgramTermination.Invoke(block, new object[] { MyProgrammableBlock.ScriptTerminationReason.InstructionOverflow });
        }

        private static FieldInfo GetField(string name)
        {
            return typeof(MyProgrammableBlock).GetField(name, InstanceFlags)
                ?? throw new InvalidOperationException($"Could not find MyProgrammableBlock.{name}, the game has probably changed");
        }

        private static Func<MyProgrammableBlock, T> CompileGetter<T>(string fieldName)
        {
            ParameterExpression block = Expression.Parameter(typeof(MyProgrammableBlock));
            Expression value = Expression.Convert(Expression.Field(block, GetField(fieldName)), typeof(T));
            return Expression.Lambda<Func<MyProgrammableBlock, T>>(value, block).Compile();
        }
    }
}
