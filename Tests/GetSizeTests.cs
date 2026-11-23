using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    /// <summary>
    /// The memory walk on hostile script types: a chain of thousands of nested structs, which a script of under 100,000
    /// characters can declare, must not run the walk's thread out of stack, which would end the server process.
    /// </summary>
    public sealed class GetSizeTests
    {
        private const long Limit = 10_240_000;

        /// <summary>
        /// An assembly like a script's: structs S0 to S{depth}, each holding the one before; a Program holding the deepest
        /// as an instance field when <paramref name="instanceField"/>, else an unused Decoy class holding it in a static
        /// field with no static initializer.
        /// </summary>
        private static (object Program, Assembly Script) Script(int depth, bool instanceField)
        {
            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NestedStructs" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
            ModuleBuilder module = assembly.DefineDynamicModule("Script");

            TypeBuilder first = module.DefineType("S0", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
            first.DefineField("list", typeof(List<byte[]>), FieldAttributes.Public);
            Type previous = first.CreateTypeInfo()!.AsType();
            for (int level = 1; level <= depth; level++)
            {
                TypeBuilder next = module.DefineType("S" + level.ToString(System.Globalization.CultureInfo.InvariantCulture), TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
                next.DefineField("a", previous, FieldAttributes.Public);
                previous = next.CreateTypeInfo()!.AsType();
            }

            TypeBuilder decoy = module.DefineType("Decoy", TypeAttributes.Public);
            decoy.DefineField("x", previous, FieldAttributes.Public | FieldAttributes.Static);
            decoy.CreateTypeInfo();

            TypeBuilder program = module.DefineType("Program", TypeAttributes.Public);
            if (instanceField)
                program.DefineField("held", previous, FieldAttributes.Public);
            program.DefineDefaultConstructor(MethodAttributes.Public);
            Type programType = program.CreateTypeInfo()!.AsType();

            return (Activator.CreateInstance(programType)!, programType.Assembly);
        }

        /// <summary>The walk on a pool-sized thread with a 1 MB stack, the smallest a host gives it.</summary>
        private static long MeasureOnSmallStack(object program, Assembly script)
        {
            long size = -1;
            Exception? failure = null;
            Thread thread = new(() =>
            {
                try
                {
                    size = GetSize.Of(program, script, Limit, out _, out _);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            }, 1024 * 1024);
            thread.Start();
            thread.Join();
            Assert.Null(failure);
            return size;
        }

        /// <summary>
        /// Structs that each hold two of the one before, S0 holding an object: S{levels} is 8 * 2^levels bytes and has about
        /// 2^(levels + 1) fields to walk. Returns a Program holding one as an instance field, and the struct type.
        /// </summary>
        private static (object? Program, Assembly Script, Exception? LoadFailure) DoublingStructs(int levels)
        {
            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DoublingStructs" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
            ModuleBuilder module = assembly.DefineDynamicModule("Script");
            try
            {
                TypeBuilder first = module.DefineType("S0", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
                first.DefineField("a", typeof(object), FieldAttributes.Public);
                Type previous = first.CreateTypeInfo()!.AsType();
                for (int level = 1; level <= levels; level++)
                {
                    TypeBuilder next = module.DefineType("S" + level.ToString(System.Globalization.CultureInfo.InvariantCulture), TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
                    next.DefineField("a", previous, FieldAttributes.Public);
                    next.DefineField("b", previous, FieldAttributes.Public);
                    previous = next.CreateTypeInfo()!.AsType();
                }

                TypeBuilder program = module.DefineType("Program", TypeAttributes.Public);
                program.DefineField("held", previous, FieldAttributes.Public);
                program.DefineDefaultConstructor(MethodAttributes.Public);
                Type programType = program.CreateTypeInfo()!.AsType();
                return (Activator.CreateInstance(programType)!, programType.Assembly, null);
            }
            catch (TypeLoadException e)
            {
                return (null, assembly, e);
            }
        }

        /// <summary>
        /// About 8 MB of struct, under the default limit, holding two copies of a struct holding two copies, 20 levels down,
        /// with about a million references in it: read in place with no struct copied, its two million nested structs below
        /// the free depth charged a step each, so the scan is quick and its figure is never under the real size.
        /// </summary>
        [Fact]
        public void StructsWithMillionsOfNestedStructsAreWalkedQuickly()
        {
            (object? program, Assembly script, _) = DoublingStructs(20);
            Assert.NotNull(program);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            long size = MeasureOnSmallStack(program!, script);
            Assert.True(size > 8_000_000, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(watch.Elapsed.TotalSeconds < 2, watch.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
        }

        /// <summary>
        /// A struct too large for the runtime to load (128 MB): the check counts the script as over the limit, without
        /// throwing, so no other block's check is stopped.
        /// </summary>
        public sealed class Holder
        {
            public object? Held;
        }

        public struct OneReference
        {
            public object? A;
        }

        public struct TwoStrings
        {
            public string? A;
            public string? B;
        }

        public struct Nested
        {
            public OneReference X;
        }

        private static long Measure(object held)
        {
            Holder holder = new() { Held = held };
            long size = GetSize.Of(holder, typeof(Holder).Assembly, Limit, out _, out string? unmeasured);
            Assert.True(unmeasured is null, unmeasured + ", counted as not measurable at " + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes");
            return size;
        }

        public struct C1 { public object? A; }
        public struct C2 { public C1 A; }
        public struct C3 { public C2 A; }
        public struct C4 { public C3 A; }
        public struct C5 { public C4 A; }
        public struct S0 { public string? S; }
        public struct W1 { public S0 I; }
        public struct Rec { public W1 A; public W1 B; }
        public struct Pair { public string? X; public string? Y; }
        public struct Tup { public Pair A; public string? B; }

        /// <summary>
        /// Nested reference-holding structs near the limit, which the walk once charged a level at a time: chains three and
        /// five deep, a struct of two wrapped strings, and a list of key-value pairs of nested tuples, all measured.
        /// </summary>
        [Fact]
        public void NestedStructsNearTheLimitAreMeasured()
        {
            Assert.True(Measure(new C3[(int)(Limit * 9 / 10 / 8)]) < Limit);
            Assert.True(Measure(new C5[(int)(Limit * 9 / 10 / 8)]) < Limit);

            Rec[] records = new Rec[(int)(Limit * 9 / 10 / 16)];
            for (int index = 0; index < records.Length; index++)
                records[index] = new Rec { A = new W1 { I = new S0 { S = "s" } }, B = new W1 { I = new S0 { S = "t" } } };
            Assert.True(Measure(records) < Limit);

            List<KeyValuePair<string, Tup>> pairs = new(250_000);
            for (int index = 0; index < 250_000; index++)
                pairs.Add(new KeyValuePair<string, Tup>("k", new Tup { A = new Pair { X = "x", Y = "y" }, B = "b" }));
            Assert.True(Measure(pairs) < Limit);
        }

        /// <summary>The cases the reviewers' harnesses flagged: arrays of reference-holding structs well under the limit are measured, not counted as over.</summary>
        [Fact]
        public void LargeArraysOfStructsUnderTheLimitAreMeasured()
        {
            Assert.True(Measure(new OneReference[1_000_000]) < Limit); // 8 MB, all null.
            Assert.True(Measure(new Nested[600_000]) < Limit); // 4.8 MB.

            TwoStrings[] pairs = new TwoStrings[400_000]; // 6.4 MB of structs, the strings shared.
            for (int index = 0; index < pairs.Length; index++)
                pairs[index] = new TwoStrings { A = "a", B = "b" };
            Assert.True(Measure(pairs) < Limit);
        }

        /// <summary>
        /// A delegate doubled into millions of entries with +=: the list is read in place, not copied, charged to the budget
        /// and counted in the size, so the script is over the limit, and quickly.
        /// </summary>
        [Fact]
        public void ADelegateDoubledIntoMillionsOfEntriesIsCountedAndQuick()
        {
            Action action = () => { };
            for (int doubling = 0; doubling < 22; doubling++)
                action += action; // 4 million entries, 32 MB of list.
            Holder holder = new() { Held = action };
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            long size = GetSize.Of(holder, typeof(Holder).Assembly, Limit, out _, out _);
            Assert.True(size > Limit, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(watch.Elapsed.TotalSeconds < 1, watch.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
        }

        [Fact]
        public void ADelegateWithAFewEntriesIsMeasured()
        {
            Action action = () => { };
            action += () => { };
            action += () => { };
            Holder holder = new() { Held = action };
            long size = GetSize.Of(holder, typeof(Holder).Assembly, Limit, out _, out string? unmeasured);
            Assert.Null(unmeasured);
            Assert.True(size > 0 && size < 10_000, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Runs on the memory check's own thread, as TrackingManager runs a pass.</summary>
        private static void OnScanThread(Action action)
        {
            Exception? failure = null;
            Thread thread = GetSize.ScanThread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            thread.Start();
            thread.Join();
            Assert.Null(failure);
        }

        /// <summary>
        /// The attack the runtime's own type loader falls to: 5,000 structs declared deepest first, each holding the next,
        /// in an unused class's static field (DeepStructsFixture, about 100,000 characters as a script). The walk reads no
        /// statics and loads no type the script has not, so the chain is never loaded and the script is measured.
        /// </summary>
        [Fact]
        public void AnUnusedChainOfStructsIsNeverLoaded()
        {
            // Naming the Program type loads only that type, not the chain.
            Assembly script = typeof(DeepStructsFixture.Program).Assembly;
            object program = new DeepStructsFixture.Program();

            long size = MeasureOnSmallStack(program, script);
            Assert.True(size > 0 && size < 1_000, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A field, left unset, of the last of 3,000 classes each deriving from the one before: resolving its type would
        /// load the chain, which takes Reflection 17 seconds and more, and on a small stack overflows it. Read by its
        /// offset, it is never resolved.
        /// </summary>
        [Fact]
        public void AnUnsetFieldOfAClassAtTheEndOfALongChainIsNotResolved()
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            long size = MeasureOnSmallStack(new DeepFieldTypeFixture.ChainProgram(), typeof(DeepFieldTypeFixture.ChainProgram).Assembly);
            Assert.True(size > 0 && size < 1_000, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(watch.Elapsed.TotalSeconds < 1, watch.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
        }

        [Fact]
        public void FieldsAreReadByOffsetOnThisRuntime() => Assert.True(GetSize.Available);

        [Fact]
        public void AStructTooLargeToLoadIsRefusedByTheRuntime()
        {
            (object? program, _, Exception? loadFailure) = DoublingStructs(24);
            // The 128 MB struct: a script's assembly is compiled without loading it, so the memory check is the first to.
            Assert.Null(program);
            Assert.IsAssignableFrom<TypeLoadException>(loadFailure);
        }

        /// <summary>
        /// A script with a type that can't be loaded, as the 128 MB struct above can't, in an unset array field of the
        /// instance or of an unused class's static field: the walk resolves neither, so the script is measured. The type
        /// here is one that was never completed.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AScriptWithAnUnusedTypeThatCannotLoadIsMeasured(bool instanceField)
        {
            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Unloadable" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
            ModuleBuilder module = assembly.DefineDynamicModule("Script");
            TypeBuilder broken = module.DefineType("Broken", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
            broken.DefineField("a", typeof(object), FieldAttributes.Public);
            // Never created, so the runtime can't load it.

            TypeBuilder decoy = module.DefineType("Decoy", TypeAttributes.Public);
            if (!instanceField)
                decoy.DefineField("x", broken.MakeArrayType(), FieldAttributes.Public | FieldAttributes.Static);
            decoy.CreateTypeInfo();

            TypeBuilder program = module.DefineType("Program", TypeAttributes.Public);
            if (instanceField)
                program.DefineField("held", broken.MakeArrayType(), FieldAttributes.Public);
            program.DefineDefaultConstructor(MethodAttributes.Public);
            Type programType = program.CreateTypeInfo()!.AsType();

            long size = MeasureOnSmallStack(Activator.CreateInstance(programType)!, programType.Assembly);
            Assert.True(size > 0 && size < Limit, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void WithNoLimitAScriptThatCannotBeMeasuredCountsAsAFiniteSize()
        {
            (object program, Assembly script) = Script(5000, instanceField: true);
            long size = GetSize.Of(program, script, 0, out _, out string? unmeasured);
            Assert.Equal(GetSize.TooDeep, unmeasured);
            Assert.Equal(GetSize.UNMEASURABLE_BYTES, size);
            Assert.True(size * 1000 > size); // Many such blocks still add up without overflowing.
        }

        [Fact]
        public void StructsNestedThousandsDeepAreCountedAsOverTheLimitWithoutAStackOverflow()
        {
            (object program, Assembly script) = Script(5000, instanceField: true);
            Assert.True(MeasureOnSmallStack(program, script) > Limit);
        }

        [Fact]
        public void StructsNestedThousandsDeepInAStaticFieldAreNotRead()
        {
            (object program, Assembly script) = Script(5000, instanceField: false);
            long size = MeasureOnSmallStack(program, script);
            Assert.True(size > 0 && size < Limit, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void StructsNestedAsRealScriptsDoAreMeasured()
        {
            (object program, Assembly script) = Script(20, instanceField: true);
            long size = MeasureOnSmallStack(program, script);
            Assert.True(size > 0 && size < Limit, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void NestingJustInsideTheCapIsMeasured()
        {
            (object program, Assembly script) = Script(GetSize.MAX_STRUCT_DEPTH - 10, instanceField: true);
            long size = MeasureOnSmallStack(program, script);
            Assert.True(size > 0 && size < Limit, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public readonly struct Entry
        {
            public readonly string Key;
            private readonly byte[] _value;

            public Entry(string key, byte[] value)
            {
                Key = key;
                _value = value;
            }

            public int Length => _value.Length;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct Wrapper
        {
            public readonly Entry Inner;
            public byte[]? Loose;

            public Wrapper(Entry inner)
            {
                Inner = inner;
                Loose = null;
            }
        }

        public sealed class ReadonlyHolder
        {
            public readonly Wrapper Wrapped;
            public readonly Entry Direct;
            private readonly Wrapper[] _many;

            public ReadonlyHolder(int bytes)
            {
                Wrapped = new Wrapper(new Entry("a", new byte[bytes])) { Loose = new byte[bytes] };
                Direct = new Entry("b", new byte[bytes]);
                _many = new[] { new Wrapper(new Entry("c", new byte[bytes])) };
            }

            public int Count => _many.Length;
        }

        /// <summary>
        /// References in readonly struct fields, in place, nested and in an array, are all read, by their offsets: every
        /// array they hold is counted.
        /// </summary>
        [Fact]
        public void ReferencesInReadonlyStructFieldsAreCounted()
        {
            const int bytes = 100_000;
            long size = 0;
            string? unmeasured = "not run";
            OnScanThread(() => size = GetSize.Of(new ReadonlyHolder(bytes), typeof(ReadonlyHolder).Assembly, Limit, out _, out unmeasured));
            Assert.Null(unmeasured);
            Assert.True(size >= 4L * bytes, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public sealed class NullableHolder
        {
            public Entry?[] Entries = { new Entry("a", new byte[100_000]), null, new Entry("b", new byte[100_000]) };
            public Entry? One = new Entry("c", new byte[100_000]);
            public List<KeyValuePair<string, byte[]>> Pairs = new() { new KeyValuePair<string, byte[]>("d", new byte[100_000]) };
        }

        /// <summary>A Nullable boxes as its value: an array of them, a field of one, and a list of KeyValuePairs are all counted.</summary>
        [Fact]
        public void NullableStructsAndKeyValuePairsAreCounted()
        {
            long size = 0;
            string? unmeasured = "not run";
            OnScanThread(() => size = GetSize.Of(new NullableHolder(), typeof(NullableHolder).Assembly, Limit, out _, out unmeasured));
            Assert.Null(unmeasured);
            Assert.True(size >= 400_000, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Unset fields of arrays of arrays 600 and 20 levels deep: a reference's type is never resolved, so neither costs
        /// anything and both scripts are measured.
        /// </summary>
        [Fact]
        public void FieldsOfArraysNestedDeepAreNotResolved()
        {
            long size = 0;
            string? unmeasured = "not run";
            OnScanThread(() => size = GetSize.Of(new DeepFieldTypeFixture.DeepProgram(), typeof(DeepFieldTypeFixture.DeepProgram).Assembly, Limit, out _, out unmeasured));
            Assert.Null(unmeasured);
            Assert.True(size > 0 && size < Limit);

            OnScanThread(() => size = GetSize.Of(new DeepFieldTypeFixture.RealProgram(), typeof(DeepFieldTypeFixture.RealProgram).Assembly, Limit, out _, out unmeasured));
            Assert.Null(unmeasured);
            Assert.True(size > 0 && size < Limit);
        }

        [Fact]
        public void TheWalkWorksAgainAfterADeepScript()
        {
            (object deep, Assembly deepScript) = Script(5000, instanceField: true);
            Assert.True(MeasureOnSmallStack(deep, deepScript) > Limit);
            (object plain, Assembly plainScript) = Script(3, instanceField: true);
            long size = MeasureOnSmallStack(plain, plainScript);
            Assert.True(size > 0 && size < Limit);
        }
    }
}
