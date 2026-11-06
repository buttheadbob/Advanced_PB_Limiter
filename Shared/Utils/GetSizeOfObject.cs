using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Sandbox.ModAPI;

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
    /// </summary>
    public static class GetSize
    {
        private const int MAX_OBJECTS_WITHOUT_A_LIMIT = 250_000;
        private const int MIN_BYTES_PER_OBJECT = 8; // Each step of the budget adds at least this to the size.
        private const int KEEP_BUFFERS_BELOW = 16_384;
        private const long OBJECT_HEADER_BYTES = 16;
        private const long ARRAY_HEADER_BYTES = 24;
        private const long STRING_HEADER_BYTES = 22;
        private const BindingFlags DeclaredInstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private sealed class TypeLayout
        {
            public long FieldBytes;
            public FieldInfo[] ReferenceFields = Array.Empty<FieldInfo>();
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        // Weak keys: recompiled scripts bring new types, and this must not keep the old ones alive.
        private static readonly ConditionalWeakTable<Type, TypeLayout> Layouts = new();

        // Reused between scans. Only one scan runs at a time, TrackingManager makes sure of that.
        private static HashSet<object> _visited = new(new ReferenceComparer());
        private static readonly Stack<object> _pending = new();

        /// <param name="program">The script to measure.</param>
        /// <param name="limit">Stop once the size is over this, in bytes. 0 for no limit.</param>
        /// <param name="time">The Stopwatch ticks the scan took.</param>
        /// <returns>The estimated size in bytes. Over the limit it is only a lower bound.</returns>
        public static long OfProgram(IMyGridProgram program, long limit, out long time)
        {
            long start = Stopwatch.GetTimestamp();
            long size = 0;
            Assembly script = program.GetType().Assembly;

            // Enough to always reach the limit, so nobody gets under it by holding a great many small objects.
            int budget = limit > 0
                ? (int)Math.Min(limit / MIN_BYTES_PER_OBJECT + 1, int.MaxValue)
                : MAX_OBJECTS_WITHOUT_A_LIMIT;

            try
            {
                Visit(program, script);
                while (_pending.Count > 0 && budget > 0 && (limit <= 0 || size <= limit))
                {
                    budget--;
                    size += Measure(_pending.Pop(), script, ref budget);
                }
            }
            finally
            {
                // These must not keep the script's objects alive until the next scan.
                _pending.Clear();
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
                    foreach (Delegate single in action.GetInvocationList())
                        Visit(single.Target, script);
                    return OBJECT_HEADER_BYTES + 6 * IntPtr.Size;
            }

            TypeLayout layout = GetLayout(value.GetType());
            VisitFields(value, layout, script);
            return OBJECT_HEADER_BYTES + layout.FieldBytes;
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
                budget -= count;
                return size;
            }

            foreach (object? item in array) // Arrays of structs and multi dimensional arrays. This boxes, so the budget matters.
            {
                if (budget <= 0) break;
                budget--;
                if (item is null) continue;

                if (elementType.IsValueType)
                    VisitFields(item, GetLayout(item.GetType()), script);
                else
                    Visit(item, script);
            }

            return size;
        }

        private static void VisitFields(object value, TypeLayout layout, Assembly script)
        {
            foreach (FieldInfo field in layout.ReferenceFields)
            {
                object? fieldValue = field.GetValue(value);
                if (fieldValue is null) continue;

                if (field.FieldType.IsValueType)
                    VisitFields(fieldValue, GetLayout(fieldValue.GetType()), script);
                else
                    Visit(fieldValue, script);
            }
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
                   || (space == "System" && type.IsGenericType);
        }

        private static TypeLayout GetLayout(Type type) => Layouts.GetValue(type, CreateLayout);

        private static TypeLayout CreateLayout(Type type)
        {
            TypeLayout layout = new();
            List<FieldInfo> referenceFields = new();

            for (Type? current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(DeclaredInstanceFields))
                {
                    layout.FieldBytes += ValueBytes(field.FieldType);
                    if (ContainsReferences(field.FieldType))
                        referenceFields.Add(field);
                }
            }

            layout.ReferenceFields = referenceFields.ToArray();
            return layout;
        }

        private static bool ContainsReferences(Type type)
        {
            if (type.IsPointer) return false;
            if (!type.IsValueType) return true;
            if (type.IsPrimitive || type.IsEnum) return false;
            return GetLayout(type).ReferenceFields.Length > 0;
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
    }
}
