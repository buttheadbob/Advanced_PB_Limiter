#nullable enable
using System;
using System.Linq.Expressions;
using System.Reflection;
using Advanced_PB_Limiter.Utils;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;

namespace Advanced_PB_Limiter.Patches
{
    /// <summary>The private members of MyProgrammableBlock the limiter reads or calls.</summary>
    internal static class PbAccess
    {
        // Called on every programmable block run, so these are compiled instead of going through reflection.
        public static Func<MyProgrammableBlock, IMyGridProgram?> GetProgram { get; private set; } = NotReady<IMyGridProgram?>;
        public static Func<MyProgrammableBlock, IMyGridProgramRuntimeInfo> GetRuntime { get; private set; } = NotReady<IMyGridProgramRuntimeInfo>;

        // Only used when a block is punished.
        private static FieldInfo? _needsInstantiation;
        private static MethodInfo? _onProgramTermination;

        // Only used when Save() is skipped on a block that is turned off.
        private static FieldInfo? _storageData;

        public static void Init()
        {
            Type block = typeof(MyProgrammableBlock);
            GetProgram = CompileGetter<IMyGridProgram?>("m_instance");
            GetRuntime = CompileGetter<IMyGridProgramRuntimeInfo>("m_runtime");
            _needsInstantiation = GameMembers.Field(block, "m_needsInstantiation");
            _storageData = GameMembers.Field(block, "m_storageData");
            _onProgramTermination = GameMembers.Method(block, "OnProgramTermination", typeof(MyProgrammableBlock.ScriptTerminationReason));
        }

        /// <summary>Stops the block recompiling the script on its own. Do this before OnProgramTermination, or on its own.</summary>
        public static void ClearNeedsInstantiation(MyProgrammableBlock block)
        {
            GameMembers.Ready(_needsInstantiation).SetValue(block, false);
        }

        /// <summary>
        /// The first step of MyProgrammableBlock.UpdateStorage, without the Save() call: copies the script's Storage to the block.
        /// Recompile passes that copy to the new script, so without it a recompile goes back to an older Storage. Runs no script code.
        /// </summary>
        public static void CopyStorage(MyProgrammableBlock block)
        {
            IMyGridProgram? program = GetProgram(block);
            if (program is not null)
                GameMembers.Ready(_storageData).SetValue(block, program.Storage);
        }

        public static void TerminateProgram(MyProgrammableBlock block)
        {
            GameMembers.Ready(_onProgramTermination).Invoke(block, new object[] { MyProgrammableBlock.ScriptTerminationReason.InstructionOverflow });
        }

        private static T NotReady<T>(MyProgrammableBlock block)
        {
            throw new InvalidOperationException("Advanced PB Limiter read a programmable block before it was initialized.");
        }

        private static Func<MyProgrammableBlock, T> CompileGetter<T>(string fieldName)
        {
            ParameterExpression block = Expression.Parameter(typeof(MyProgrammableBlock));
            Expression value = Expression.Convert(Expression.Field(block, GameMembers.Field(typeof(MyProgrammableBlock), fieldName)), typeof(T));
            return Expression.Lambda<Func<MyProgrammableBlock, T>>(value, block).Compile();
        }
    }
}
