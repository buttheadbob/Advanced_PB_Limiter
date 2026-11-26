#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ExtensionMethods;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Estimates the memory a script holds by walking what it references, field by field.
    /// A script is hostile input and this runs off the game thread, so the walk:
    /// never runs script code (no property getters, enumerators, Equals or GetHashCode),
    /// is iterative with a visited set (a list holding itself cannot recurse or loop),
    /// stops as soon as the script is over the limit, as the exact size no longer matters by then,
    /// and stays out of the game: only the script's own types and the library containers it can use are followed.
    /// The script keeps running while this reads it. That is safe, it can only make the estimate slightly off.
    /// Its types are hostile too, and the walk loads none of them. Reflection resolves a field's type to read it, and the
    /// runtime then loads that type, which a script can make take minutes (a class at the end of a chain of thousands)
    /// or gigabytes (an array of arrays thousands deep) while it holds nothing in that field. So a reference is read by
    /// its offset in the value holding it (<see cref="RawFields"/>), and only the types of struct fields are resolved,
    /// which the runtime loaded to lay out the value holding them. A value read has its type loaded already. Nor does
    /// the walk read static fields, as finding them would load every type of the script. Its own recursion, in nested
    /// structs, is capped: a script that nests deeper is counted as not measurable.
    /// </summary>
    public static class GetSize
    {
        // Far deeper than any real script nests structs, and far inside the scan thread's stack.
        internal const int MAX_STRUCT_DEPTH = 256;

        /// <summary>The scan thread's stack: far more than the walk needs, its recursion being capped at <see cref="MAX_STRUCT_DEPTH"/>.</summary>
        internal const int SCAN_STACK_BYTES = 64 * 1024 * 1024;

        // Metadata signature bytes (ECMA-335 II.23.2): what a field's signature starts with, and the element types that are
        // references.
        private const byte SIGNATURE_FIELD = 0x06;
        private const byte MODIFIER_REQUIRED = 0x1F;
        private const byte MODIFIER_OPTIONAL = 0x20;
        private const byte TYPE_STRING = 0x0E;
        private const byte TYPE_VALUE = 0x11;
        private const byte TYPE_CLASS = 0x12;
        private const byte TYPE_ARRAY = 0x14;
        private const byte TYPE_GENERIC = 0x15;
        private const byte TYPE_OBJECT = 0x1C;
        private const byte TYPE_SINGLE_ARRAY = 0x1D;

        /// <summary>A thread for a memory scan, see <see cref="SCAN_STACK_BYTES"/>.</summary>
        internal static Thread ScanThread(ThreadStart start) => new(start, SCAN_STACK_BYTES)
        {
            IsBackground = true,
            Name = "Advanced PB Limiter memory check"
        };

        /// <summary>
        /// Whether the walk can read fields by their offsets on this runtime, checked once against Reflection on known types.
        /// Without it the memory check does not run, see TrackingManager.
        /// </summary>
        internal static bool Available => RawFields.Works;

        /// <summary>Why the setup behind <see cref="Available"/> failed, when it threw; null when it works or a check did not match.</summary>
        internal static Exception? UnavailableBecause => RawFields.Failure;

        // How deep the layout is in nested structs on this thread.
        [ThreadStatic]
        private static int _structDepth;

        /// <summary>What a script that can't be measured counts as with no limit set: finite, so totals can add it up.</summary>
        internal const long UNMEASURABLE_BYTES = 1L << 40;

        // Set when a scan ran out of its budget with objects or references left to walk. One scan at a time, as _visited.
        private static bool _outOfBudget;

        /// <summary>The size a script counts as when it can't be measured: just over the limit, or <see cref="UNMEASURABLE_BYTES"/> with none.</summary>
        internal static long Unmeasurable(long limit) => limit > 0 ? limit + 1 : UNMEASURABLE_BYTES;

        internal const string TooDeep = "its structs nest too deep to follow";
        internal const string CannotLoad = "one of its types can't be loaded";
        internal const string TooMuch = "it holds more than the check can follow within its budget";

        private const int MAX_OBJECTS_WITHOUT_A_LIMIT = 250_000;
        private const int MIN_BYTES_PER_OBJECT = 8; // Each step of the budget stands for at least this much of the size.
        private const int BUDGET_HEADROOM = 2;
        private const int KEEP_BUFFERS_BELOW = 16_384;
        private const long OBJECT_HEADER_BYTES = 16;
        private const long ARRAY_HEADER_BYTES = 24;
        private const long STRING_HEADER_BYTES = 22;

        private sealed class TypeLayout
        {
            public long FieldBytes;

            // The offset of every reference the value holds, those in its struct fields too, from the start of its data.
            public int[] References = Array.Empty<int>();

            // How many of them are in its struct fields: each costs a step, see Of.
            public int InStructs;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        // The layouts built in this scan, dropped at its end: a script can declare types whose layouts are as large as it
        // likes, and its assembly is never unloaded, so a cache kept between scans would keep growing with each recompile.
        private static readonly Dictionary<Type, TypeLayout> _layouts = new();

        // Reused between scans. Only one scan runs at a time, TrackingManager makes sure of that.
        private static HashSet<object> _visited = new(new ReferenceComparer());
        private static readonly Stack<object> _pending = new();

        /// <param name="program">The script's instance, where the walk starts.</param>
        /// <param name="script">The script's assembly: its types, and theirs alone, are followed.</param>
        /// <param name="limit">Stop once the size is over this, in bytes. 0 for no limit.</param>
        /// <param name="time">The Stopwatch ticks the scan took.</param>
        /// <param name="unmeasured">
        /// Why the script could not be measured, null when it was; the size is then <see cref="Unmeasurable"/>: its structs
        /// nest too deep, one of its types can't be loaded, or, with a limit, it holds more than the budget can walk.
        /// </param>
        /// <returns>
        /// The estimated size in bytes. Over the limit it is only a lower bound, and so it is with no limit when the
        /// budget ran out.
        /// </returns>
        internal static long Of(object program, Assembly script, long limit, out long time, out string? unmeasured)
        {
            long start = Stopwatch.GetTimestamp();
            long size = 0;
            unmeasured = null;

            // Each step of the budget stands for at least MIN_BYTES_PER_OBJECT bytes of the size, so twice the steps the
            // limit needs always reaches it, and no honest script runs out: an object is one step, a reference in a struct
            // held by an object one step, a struct boxed out of an array one step per 8 bytes with everything inside it read
            // within that charge, and a delegate list entry one step.
            int budget = limit > 0
                ? (int)Math.Min(limit / MIN_BYTES_PER_OBJECT * BUDGET_HEADROOM + 1, int.MaxValue)
                : MAX_OBJECTS_WITHOUT_A_LIMIT;

            _outOfBudget = false;
            try
            {
                Visit(program, script);
                while (_pending.Count > 0 && budget > 0 && (limit <= 0 || size <= limit))
                {
                    budget--;
                    size += Measure(_pending.Pop(), script, ref budget);
                }
                if (_pending.Count > 0 && budget <= 0)
                    _outOfBudget = true;
                // Not walked to the end, and not over the limit yet: it holds more than the budget can walk.
                if (_outOfBudget && limit > 0 && size <= limit)
                {
                    size = Unmeasurable(limit);
                    unmeasured = TooMuch;
                }
            }
            catch (InsufficientExecutionStackException)
            {
                size = Unmeasurable(limit);
                unmeasured = TooDeep;
            }
            catch (TypeLoadException e)
            {
                size = Unmeasurable(limit);
                unmeasured = CannotLoad + " (" + (e.TypeName ?? "").SafeName() + ")";
            }
            finally
            {
                _structDepth = 0;
                // These must not keep the script's objects or types alive until the next scan.
                _pending.Clear();
                _layouts.Clear();
                if (_visited.Count > KEEP_BUFFERS_BELOW)
                    _visited = new HashSet<object>(new ReferenceComparer());
                else
                    _visited.Clear();
                time = Stopwatch.GetTimestamp() - start;
            }

            return size;
        }

        private static long Measure(object value, Assembly script, ref int budget)
        {
            switch (value)
            {
                case string text:
                    return STRING_HEADER_BYTES + text.Length * 2L;
                case Array array:
                    return MeasureArray(array, script, ref budget);
                case StringBuilder builder:
                    return OBJECT_HEADER_BYTES + builder.Capacity * 2L;
                case Delegate action:
                    return MeasureDelegate(action, script, ref budget);
            }

            TypeLayout layout = GetLayout(value.GetType());
            // The references held directly are read within the object's step; those in its struct fields cost one each.
            if (Charge(layout.InStructs, ref budget))
                VisitReferences(value, layout, script);
            return OBJECT_HEADER_BYTES + layout.FieldBytes;
        }

        // A multicast delegate's own list, read in place: GetInvocationList copies it, and a script can double a delegate
        // into millions of entries with +=. As VRage's DelegateExtensions reads them.
        private static readonly FieldInfo? InvocationList = typeof(MulticastDelegate).GetField("_invocationList", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? InvocationCount = typeof(MulticastDelegate).GetField("_invocationCount", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// A delegate and, for a multicast one, its list: each entry is counted in the size and charged to the budget, and
        /// the delegates in it walked once each, as the visited set takes each only once.
        /// </summary>
        private static long MeasureDelegate(Delegate action, Assembly script, ref int budget)
        {
            long size = OBJECT_HEADER_BYTES + 6 * IntPtr.Size;
            if (InvocationList?.GetValue(action) is not object?[] entries)
            {
                Visit(action.Target, script);
                return size;
            }

            long count = entries.Length;
            if (InvocationCount?.GetValue(action) is IntPtr valid && (long)valid >= 0 && (long)valid < count)
                count = (long)valid;
            size += ARRAY_HEADER_BYTES + count * IntPtr.Size;
            int walked = (int)Math.Min(count, budget);
            if (walked < count)
                _outOfBudget = true;
            budget -= walked;
            for (int index = 0; index < walked; index++)
                Visit(entries[index], script);
            return size;
        }

        private static long MeasureArray(Array array, Assembly script, ref int budget)
        {
            Type elementType = array.GetType().GetElementType()!;
            long size = ARRAY_HEADER_BYTES + array.LongLength * ValueBytes(elementType);
            if (!ContainsReferences(elementType)) return size;

            if (array.Rank == 1 && array is object?[] references) // Any one dimensional array of a reference type.
            {
                int count = Math.Min(references.Length, budget);
                for (int index = 0; index < count; index++)
                    Visit(references[index], script);
                if (count < references.Length)
                    _outOfBudget = true;
                budget -= count;
                return size;
            }

            // Arrays of structs and multi dimensional arrays. Each element is a boxed copy: a step per 8 bytes of it, and
            // the references in it are read within that charge. A Nullable boxes as its value, so a box is read by its own
            // type.
            int steps = elementType.IsValueType ? StepsFor(ValueBytes(elementType)) : 1;
            foreach (object? item in array)
            {
                if (!Charge(steps, ref budget)) break;
                if (item is null) continue;

                if (elementType.IsValueType)
                    VisitReferences(item, GetLayout(item.GetType()), script);
                else
                    Visit(item, script);
            }

            return size;
        }

        private static void VisitReferences(object value, TypeLayout layout, Assembly script)
        {
            foreach (int offset in layout.References)
                Visit(RawFields.Read(value, offset), script);
        }

        private static int StepsFor(long bytes) => (int)Math.Min(int.MaxValue, Math.Max(1, bytes / MIN_BYTES_PER_OBJECT));

        /// <summary>Takes this many steps from the budget. False, with the scan marked as out of budget, when not enough is left.</summary>
        private static bool Charge(int steps, ref int budget)
        {
            if (steps > budget)
            {
                _outOfBudget = true;
                budget = 0;
                return false;
            }
            budget -= steps;
            return true;
        }

        /// <summary>
        /// One struct deeper: throws InsufficientExecutionStackException once past <see cref="MAX_STRUCT_DEPTH"/>, as
        /// RuntimeHelpers.EnsureSufficientExecutionStack does with too little stack left.
        /// </summary>
        private static void Deeper()
        {
            if (++_structDepth > MAX_STRUCT_DEPTH)
            {
                _structDepth--;
                throw new InsufficientExecutionStackException("A script's structs nest deeper than the memory check follows.");
            }
            RuntimeHelpers.EnsureSufficientExecutionStack();
        }

        private static void Visit(object? value, Assembly script)
        {
            if (value is null || !IsFollowed(value.GetType(), script)) return;
            if (_visited.Add(value))
                _pending.Push(value);
        }

        private static bool IsFollowed(Type type, Assembly script)
        {
            if (type == typeof(string) || type.IsArray || type == typeof(StringBuilder)) return true;
            if (type.Assembly == script) return true;
            if (typeof(Delegate).IsAssignableFrom(type)) return true;

            // List<>, Dictionary<,>, HashSet<>, Immutable*... and Tuple<>. The game compiles a script's collections into its
            // own MemorySafe subclasses of these, so that is what a script really holds. Everything else is the game, or too much to follow.
            string? space = type.Namespace;
            if (space is null) return false;
            return space.StartsWith("VRage.Scripting.MemorySafeTypes", StringComparison.Ordinal)
                   || space.StartsWith("System.Collections", StringComparison.Ordinal)
                   || (space == "System" && type.IsGenericType)
                   // Linq's iterators, which keep their source alive, and MyIni and the other ingame utilities.
                   || space.StartsWith("System.Linq", StringComparison.Ordinal)
                   || space == "VRage.Game.ModAPI.Ingame.Utilities";
        }

        private static TypeLayout GetLayout(Type type)
        {
            if (_layouts.TryGetValue(type, out TypeLayout? known)) return known;
            Deeper();
            try
            {
                TypeLayout layout = CreateLayout(type);
                _layouts[type] = layout;
                return layout;
            }
            finally
            {
                _structDepth--;
            }
        }

        private static TypeLayout CreateLayout(Type type)
        {
            TypeLayout layout = new();
            List<int> references = new();

            for (Type? current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetTypeInfo().DeclaredFields)
                {
                    if (field.IsStatic) continue;
                    if (IsReference(field))
                    {
                        layout.FieldBytes += IntPtr.Size;
                        references.Add(RawFields.OffsetOf(field, reference: true));
                        continue;
                    }

                    // A struct, a primitive, a pointer, or a type parameter: the runtime loaded its type to lay this one out.
                    Type fieldType = field.FieldType;
                    if (!fieldType.IsValueType)
                    {
                        layout.FieldBytes += IntPtr.Size;
                        if (!fieldType.IsPointer)
                            references.Add(RawFields.OffsetOf(field, reference: true));
                        continue;
                    }

                    layout.FieldBytes += ValueBytes(fieldType);
                    if (fieldType.IsPrimitive || fieldType.IsEnum) continue;
                    TypeLayout inner = GetLayout(fieldType);
                    if (inner.References.Length == 0) continue;
                    int offset = RawFields.OffsetOf(field, reference: false);
                    foreach (int innerOffset in inner.References)
                        references.Add(offset + innerOffset);
                    layout.InStructs += inner.References.Length;
                }
            }

            layout.References = references.ToArray();
            return layout;
        }

        /// <summary>
        /// Whether the field holds a reference, told from its metadata signature without resolving its type. False also for
        /// a type parameter, which is then resolved: its type is loaded, as is a struct field's, as a value holding it
        /// exists. A module that can't give signatures, which a script's never is, has the type resolved.
        /// </summary>
        private static bool IsReference(FieldInfo field)
        {
            byte[] signature;
            try
            {
                signature = field.Module.ResolveSignature(field.MetadataToken);
            }
            catch (NotSupportedException)
            {
                return false;
            }

            if (signature.Length < 2 || signature[0] != SIGNATURE_FIELD) return false;

            int index = 1;
            while (index < signature.Length && (signature[index] == MODIFIER_REQUIRED || signature[index] == MODIFIER_OPTIONAL))
                index += 1 + CompressedLength(signature, index + 1);
            return index < signature.Length && HoldsReference(signature, index);
        }

        /// <summary>Whether the type in a signature at <paramref name="index"/> is a reference type.</summary>
        private static bool HoldsReference(byte[] signature, int index)
        {
            switch (signature[index])
            {
                case TYPE_STRING:
                case TYPE_CLASS:
                case TYPE_ARRAY:
                case TYPE_OBJECT:
                case TYPE_SINGLE_ARRAY:
                    return true;
                case TYPE_GENERIC:
                    return index + 1 < signature.Length && signature[index + 1] == TYPE_CLASS;
                default:
                    return false;
            }
        }

        /// <summary>The length of the compressed integer at <paramref name="index"/> in a signature (ECMA-335 II.23.2).</summary>
        private static int CompressedLength(byte[] signature, int index)
        {
            if (index >= signature.Length || (signature[index] & 0x80) == 0) return 1;
            return (signature[index] & 0xC0) == 0x80 ? 2 : 4;
        }

        private static bool ContainsReferences(Type type)
        {
            if (type.IsPointer) return false;
            if (!type.IsValueType) return true;
            if (type.IsPrimitive || type.IsEnum) return false;
            return GetLayout(type).References.Length > 0;
        }

        private static long ValueBytes(Type type)
        {
            if (!type.IsValueType) return IntPtr.Size;

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean:
                case TypeCode.Byte:
                case TypeCode.SByte:
                    return 1;
                case TypeCode.Char:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                    return 2;
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Single:
                    return 4;
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Double:
                case TypeCode.DateTime:
                    return 8;
                case TypeCode.Decimal:
                    return 16;
                default:
                    return type.IsPrimitive ? IntPtr.Size : GetLayout(type).FieldBytes; // IntPtr, UIntPtr, or a struct.
            }
        }

        /// <summary>
        /// Reads a reference by its offset from the start of an object's or a boxed struct's data, so the field's type is
        /// never resolved: compiled code that names the field resolves it too. The offset is the runtime's own: on .NET 10
        /// from RuntimeFieldHandle.GetInstanceFieldOffset, on .NET Framework from the field's FieldDesc, checked to be of
        /// the kind expected. The owner check is skipped for generic types, whose FieldDesc may name a canonical
        /// instantiation; the self-check below reads the reference fields of generic classes instantiated over value types,
        /// and a reference inside one's value-type field. All of it is set
        /// up and checked once against Reflection on known types; where any of that fails, <see cref="Works"/> is false,
        /// <see cref="Failure"/> says why when it threw, and nothing is read.
        /// </summary>
        private static class RawFields
        {
            private delegate object? Reader(object holder, int offset);

            // FieldDesc (CoreCLR vm/field.h, the same in .NET Framework): the enclosing MethodTable, a DWORD of flags, then a
            // DWORD whose low 27 bits are the offset and high 5 bits the field's element type, class for every reference.
            private const int FIELD_DESC_OFFSET_BITS = 27;
            private const int FIELD_DESC_OFFSET_MASK = (1 << FIELD_DESC_OFFSET_BITS) - 1;

            /// <summary>
            /// A generic class with a value-type field and a reference field. The runtime may share a FieldDesc between
            /// compatible instantiations; the self-check reads the reference field and, for RefValue, the reference inside
            /// the value field.
            /// </summary>
            private sealed class GenericHolder<T> where T : struct
            {
                public T Value;
                public object Reference = new();
            }

            /// <summary>A value type holding a reference, to exercise a generic instantiation's value-field offset.</summary>
            private struct RefValue
            {
                public object? Value;
            }

            /// <summary>What the setup gave: the reader and the offset source, or why there are none.</summary>
            private sealed class Setup
            {
                public readonly Reader? ReadAt;
                public readonly MethodInfo? InstanceFieldOffset;
                public readonly Exception? Error;

                public Setup(Reader? readAt, MethodInfo? instanceFieldOffset, Exception? failure)
                {
                    ReadAt = readAt;
                    InstanceFieldOffset = instanceFieldOffset;
                    Error = failure;
                }
            }

            private static readonly Setup Ready = Prepare();

            internal static bool Works => Ready.ReadAt is not null;

            internal static Exception? Failure => Ready.Error;

            internal static object? Read(object holder, int offset) => Ready.ReadAt!(holder, offset);

            /// <summary>
            /// The field's offset from the start of its holder's data. Throws InvalidOperationException when the runtime's
            /// figure does not look like this field's. Generic types skip the owner check; the self-check reads the reference
            /// fields of generic classes over value types, and a reference inside one's value-type field.
            /// </summary>
            internal static int OffsetOf(FieldInfo field, bool reference)
            {
                if (!Works) throw new InvalidOperationException("The memory check can't read fields on this runtime.");
                int offset = RuntimeOffset(Ready.InstanceFieldOffset, field, reference);
                if (offset < 0)
                    throw new InvalidOperationException("The runtime gave no offset for the field " + field.Name.SafeName() + ".");
                return offset;
            }

            /// <summary>
            /// Sets up the reader and checks it, catching anything: a failure leaves the memory check off, never the plugin.
            /// Self-check reads are bounded to the holder's field bytes.
            /// </summary>
            private static Setup Prepare()
            {
                try
                {
                    MethodInfo? instanceFieldOffset = typeof(RuntimeFieldHandle).GetMethod("GetInstanceFieldOffset", BindingFlags.NonPublic | BindingFlags.Static);
                    Reader readAt = CompileReader();
                    return Check(readAt, instanceFieldOffset)
                        ? new Setup(readAt, instanceFieldOffset, null)
                        : new Setup(null, null, null);
                }
                catch (Exception e)
                {
                    return new Setup(null, null, e);
                }
            }

            /// <summary>The runtime's offset for the field, or -1 when it can't be had or does not look like this field's.</summary>
            private static int RuntimeOffset(MethodInfo? instanceFieldOffset, FieldInfo field, bool reference)
            {
                if (instanceFieldOffset is not null)
                    return instanceFieldOffset.Invoke(null, new object[] { field }) is int offset ? offset : -1;

                Type declaring = field.DeclaringType!;
                IntPtr desc = field.FieldHandle.Value;
                // A generic type's fields may be shared between compatible instantiations, so their FieldDesc may name a
                // canonical type; value-type instantiations have their own.
                if (!declaring.IsGenericType && !NamesType(desc, declaring.TypeHandle.Value)) return -1;
                int bits = Marshal.ReadInt32(desc, IntPtr.Size + 4);
                int elementType = (int)((uint)bits >> FIELD_DESC_OFFSET_BITS);
                if (reference ? elementType != TYPE_CLASS : elementType != TYPE_VALUE) return -1;
                return bits & FIELD_DESC_OFFSET_MASK;
            }

            /// <summary>
            /// Whether the FieldDesc names this MethodTable: .NET Framework keeps it as a pointer relative to the FieldDesc's
            /// own address, other builds as a plain pointer.
            /// </summary>
            private static bool NamesType(IntPtr desc, IntPtr methodTable)
            {
                IntPtr stored = Marshal.ReadIntPtr(desc);
                return stored == methodTable || new IntPtr(desc.ToInt64() + stored.ToInt64()) == methodTable;
            }

            private static Reader CompileReader()
            {
                // StrongBox<byte>.Value is the only field of its class, so its address is the start of any object's data.
                FieldInfo? dataStart = typeof(StrongBox<byte>).GetField(nameof(StrongBox<byte>.Value));
                if (dataStart is null) throw new MissingFieldException(nameof(StrongBox<byte>), nameof(StrongBox<byte>.Value));

                // The address of the holder's data, plus the offset, read as a reference. A managed pointer throughout, so
                // the collector can move the holder meanwhile.
                DynamicMethod method = new("ReadAt", typeof(object), new[] { typeof(object), typeof(int) }, typeof(GetSize).Module, skipVisibility: true);
                ILGenerator il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, dataStart);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldind_Ref);
                il.Emit(OpCodes.Ret);
                return (Reader)method.CreateDelegate(typeof(Reader));
            }

            /// <summary>
            /// Reads every reference field of known objects by its offset and compares it with Reflection: a class, a
            /// generic class holding a struct, and a boxed struct.
            /// </summary>
            private static bool Check(Reader readAt, MethodInfo? instanceFieldOffset)
            {
                object first = new();
                object second = new();
                object nested = new();
                KeyValuePair<long, object> pair = new(7, second);
                GenericHolder<byte> byteHolder = new() { Reference = first };
                GenericHolder<Guid> guidHolder = new() { Reference = second };
                GenericHolder<RefValue> refHolder = new() { Value = new RefValue { Value = nested }, Reference = first };
                return Matches(readAt, instanceFieldOffset, Tuple.Create(first, "text", 5L, pair, new object[1]))
                       && Matches(readAt, instanceFieldOffset, pair)
                       && Matches(readAt, instanceFieldOffset, new StringBuilder("text", 64))
                       && Matches(readAt, instanceFieldOffset, byteHolder)
                       && Matches(readAt, instanceFieldOffset, guidHolder)
                       && Matches(readAt, instanceFieldOffset, refHolder)
                       && NestedGeneric(readAt, instanceFieldOffset, refHolder, nested)
                       && Nested(readAt, instanceFieldOffset, Tuple.Create(1, pair), second);
            }

            private static bool Matches(Reader readAt, MethodInfo? instanceFieldOffset, object holder)
            {
                for (Type? current = holder.GetType(); current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
                {
                    foreach (FieldInfo field in current.GetTypeInfo().DeclaredFields)
                    {
                        if (field.IsStatic || field.FieldType.IsValueType || field.FieldType.IsPointer) continue;
                        int offset = RuntimeOffset(instanceFieldOffset, field, reference: true);
                        if (offset < 0 || !Readable(holder, offset) || !ReferenceEquals(readAt(holder, offset), field.GetValue(holder))) return false;
                    }
                }
                return true;
            }

            /// <summary>A reference inside a value-type field of a generic instantiation, at the value field's offset plus the reference's in the struct.</summary>
            private static bool NestedGeneric(Reader readAt, MethodInfo? instanceFieldOffset, GenericHolder<RefValue> holder, object expected)
            {
                FieldInfo? outer = typeof(GenericHolder<RefValue>).GetField(nameof(GenericHolder<RefValue>.Value), BindingFlags.Public | BindingFlags.Instance);
                FieldInfo? inner = typeof(RefValue).GetField(nameof(RefValue.Value), BindingFlags.Public | BindingFlags.Instance);
                if (outer is null || inner is null) return false;
                int outerOffset = RuntimeOffset(instanceFieldOffset, outer, reference: false);
                int innerOffset = RuntimeOffset(instanceFieldOffset, inner, reference: true);
                return outerOffset >= 0 && innerOffset >= 0 && Readable(holder, outerOffset + innerOffset)
                       && ReferenceEquals(readAt(holder, outerOffset + innerOffset), expected);
            }

            /// <summary>A reference in a struct held by an object, at the struct field's offset plus the reference's in the struct.</summary>
            private static bool Nested(Reader readAt, MethodInfo? instanceFieldOffset, Tuple<int, KeyValuePair<long, object>> holder, object expected)
            {
                FieldInfo? outer = holder.GetType().GetField("m_Item2", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo? inner = typeof(KeyValuePair<long, object>).GetField("value", BindingFlags.NonPublic | BindingFlags.Instance);
                if (outer is null || inner is null) return false;
                int outerOffset = RuntimeOffset(instanceFieldOffset, outer, reference: false);
                int innerOffset = RuntimeOffset(instanceFieldOffset, inner, reference: true);
                return outerOffset >= 0 && innerOffset >= 0 && Readable(holder, outerOffset + innerOffset)
                       && ReferenceEquals(readAt(holder, outerOffset + innerOffset), expected);
            }

            /// <summary>Whether a reference-sized read at the offset stays inside the holder's field bytes.</summary>
            private static bool Readable(object holder, int offset)
            {
                int bytes = DataBytes(holder.GetType());
                return offset >= 0 && offset <= bytes - IntPtr.Size;
            }

            /// <summary>The holder's field bytes, rounded to pointer size, without reading field offsets.</summary>
            private static int DataBytes(Type type)
            {
                int bytes = 0;
                for (Type? current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
                {
                    foreach (FieldInfo field in current.GetTypeInfo().DeclaredFields)
                    {
                        if (field.IsStatic) continue;
                        bytes += field.FieldType.IsValueType ? SimpleValueBytes(field.FieldType) : IntPtr.Size;
                    }
                }
                return (bytes + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
            }

            /// <summary>The size of a value type's fields, rounded to pointer size, without reading field offsets.</summary>
            private static int SimpleValueBytes(Type type)
            {
                if (!type.IsValueType) return IntPtr.Size;

                switch (Type.GetTypeCode(type))
                {
                    case TypeCode.Boolean:
                    case TypeCode.Byte:
                    case TypeCode.SByte:
                        return 1;
                    case TypeCode.Char:
                    case TypeCode.Int16:
                    case TypeCode.UInt16:
                        return 2;
                    case TypeCode.Int32:
                    case TypeCode.UInt32:
                    case TypeCode.Single:
                        return 4;
                    case TypeCode.Int64:
                    case TypeCode.UInt64:
                    case TypeCode.Double:
                    case TypeCode.DateTime:
                        return 8;
                    case TypeCode.Decimal:
                        return 16;
                    default:
                        if (type.IsPrimitive) return IntPtr.Size;
                        int bytes = 0;
                        for (Type? current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
                        {
                            foreach (FieldInfo field in current.GetTypeInfo().DeclaredFields)
                            {
                                if (field.IsStatic) continue;
                                bytes += field.FieldType.IsValueType ? SimpleValueBytes(field.FieldType) : IntPtr.Size;
                            }
                        }
                        return (bytes + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
                }
            }
        }
    }
}
