using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Advanced_PB_Limiter.Utils;
using Xunit;
using Xunit.Abstractions;

namespace Advanced_PB_Limiter.Tests
{
    /// <summary>
    /// What a run is charged with RunClock, as PbProfiler takes it around each run: the game's own wall time less the runs
    /// nested in it, nothing left out, and whether a collection happened, which only the instant kill looks at.
    /// </summary>
    public sealed class RunClockTests
    {
        private readonly ITestOutputHelper _output;

        public RunClockTests(ITestOutputHelper output) => _output = output;

        /// <summary>A run as the patches see it: the stamp, the script's work, and the game's wall time for it handed back.</summary>
        private static double Run(Action script, out double measuredMs, out bool collected)
        {
            object program = new();
            RunStamp stamp = RunClock.Start(program, -1, endedThisFrame: false);
            Stopwatch measured = Stopwatch.StartNew();
            script();
            measuredMs = measured.Elapsed.TotalMilliseconds;
            return RunClock.Finish(in stamp, measuredMs, program, endedThisFrame: true, out collected);
        }

        private static string Ms(double value) => value.ToString("0.00", CultureInfo.InvariantCulture) + "ms";

        // Kept so the work is not optimised away.
        private double _sink;

        private void Spin(int count)
        {
            double sum = 0;
            for (int index = 0; index < count; index++)
                sum += Math.Sqrt(index);
            _sink += sum;
            Assert.True(_sink >= 0);
        }

        [Fact]
        public void ARunWithoutAStampKeepsTheMeasuredTime()
        {
            Assert.Equal(12.5, RunClock.Finish(default, 12.5, null, false, out bool collected));
            Assert.False(collected);
        }

        /// <summary>
        /// The first step of creating a script's instance goes through the same call as a run, before the game resets its
        /// figure, so the figure is still the old script's last run: it is not charged, whether there was no instance before
        /// or the old one was still in place, as in a recompile.
        /// </summary>
        [Fact]
        public void ACallThatPutANewInstanceInPlaceIsNotARun()
        {
            object old = new();
            object created = new();
            RunStamp stamp = RunClock.Start(null, 4.25, endedThisFrame: false);
            Assert.Equal(0, RunClock.Finish(in stamp, 4.25, created, endedThisFrame: false, out _));
            stamp = RunClock.Start(old, 4.25, endedThisFrame: false);
            Assert.Equal(0, RunClock.Finish(in stamp, 4.25, created, endedThisFrame: false, out _));
        }

        /// <summary>
        /// A call in which Main() did not reach its end while the script was kept, a TryRun nested too deep say, leaves the
        /// last run's figure: it is not charged again, whether or not the block already ran this frame.
        /// </summary>
        [Fact]
        public void ACallThatDidNotEndARunIsNotCharged()
        {
            object program = new();
            RunStamp stamp = RunClock.Start(program, 8, endedThisFrame: false);
            Assert.Equal(0, RunClock.Finish(in stamp, 8, program, endedThisFrame: false, out _));
            stamp = RunClock.Start(program, 8, endedThisFrame: true);
            Assert.Equal(0, RunClock.Finish(in stamp, 8, program, endedThisFrame: true, out _));
        }

        /// <summary>
        /// A run is charged with a new figure, and with exactly the last one's, as two short runs of the same tick count have,
        /// when it ended in this frame and the last one had not.
        /// </summary>
        [Fact]
        public void ARunIsChargedAlsoWithTheSameFigure()
        {
            object program = new();
            RunStamp stamp = RunClock.Start(program, 4.25, endedThisFrame: true);
            Assert.Equal(4.5, RunClock.Finish(in stamp, 4.5, program, endedThisFrame: true, out _));
            stamp = RunClock.Start(program, 4.25, endedThisFrame: false);
            Assert.Equal(4.25, RunClock.Finish(in stamp, 4.25, program, endedThisFrame: true, out _));
        }

        [Fact]
        public void APlainRunIsChargedItsWallTime()
        {
            double charged = Run(() => Spin(1_000_000), out double measured, out _);
            Assert.Equal(measured, charged);
        }

        /// <summary>
        /// Heavy work handed to another thread while the script waits for it, as `Action a = list.Sort; a.EndInvoke(a.BeginInvoke(null, null))`
        /// does on .NET Framework (BeginInvoke runs on a pool thread there; .NET 10 refuses it, so a thread stands in): the
        /// run is charged the whole wait, as the game measures it.
        /// </summary>
        [Fact]
        public void WorkWaitedForOnAnotherThreadIsCharged()
        {
            List<int> list = new(3_000_000);
            Random random = new(1);
            for (int index = 0; index < 3_000_000; index++)
                list.Add(random.Next());

            double charged = Run(() =>
            {
                Action sort = list.Sort;
                try
                {
                    sort.EndInvoke(sort.BeginInvoke(null, null));
                }
                catch (PlatformNotSupportedException)
                {
                    Thread worker = new(() => sort());
                    worker.Start();
                    worker.Join();
                }
            }, out double measured, out _);

            _output.WriteLine("charged " + Ms(charged) + " of " + Ms(measured));
            Assert.Equal(measured, charged);
            Assert.True(charged > 10, Ms(charged));
        }

        /// <summary>A script running another block through TryRun: the inner run is charged to the inner block, and taken off the outer one.</summary>
        [Fact]
        public void ANestedRunIsChargedToItsOwnBlockOnly()
        {
            double inner = 0, innerMeasured = 0;
            double outer = Run(() =>
            {
                Spin(1_000_000);
                inner = Run(() => Spin(3_000_000), out innerMeasured, out _);
                Spin(1_000_000);
            }, out double outerMeasured, out _);

            _output.WriteLine("inner " + Ms(inner) + ", outer " + Ms(outer) + " of " + Ms(outerMeasured));
            Assert.Equal(innerMeasured, inner);
            Assert.Equal(outerMeasured - innerMeasured, outer, 6);
            Assert.True(outer > 0 && outer < outerMeasured);
        }

        [Fact]
        public void TwoNestedRunsInARowAreBothTakenOff()
        {
            double first = 0, second = 0;
            double outer = Run(() =>
            {
                Run(() => Spin(1_000_000), out first, out _);
                Run(() => Spin(1_000_000), out second, out _);
                Spin(500_000);
            }, out double outerMeasured, out _);

            Assert.Equal(outerMeasured - first - second, outer, 6);
        }

        /// <summary>A run with a collection in it is charged in full and reported as collected, so only the instant kill passes it over.</summary>
        [Fact]
        public void ACollectedRunIsChargedInFullAndReported()
        {
            double charged = Run(() =>
            {
                for (int index = 0; index < 20; index++)
                    GC.KeepAlive(new byte[10_000_000]);
                Spin(1_000_000);
            }, out double measured, out bool collected);

            Assert.True(collected);
            Assert.Equal(measured, charged);
        }

        [Fact]
        public void ARunWithoutACollectionIsNotReportedAsCollected()
        {
            // Warmed up first, so the run itself allocates nothing new.
            Spin(10);
            bool collected = true;
            for (int attempt = 0; attempt < 5 && collected; attempt++)
                Run(() => Spin(100_000), out _, out collected);
            Assert.False(collected);
        }
    }
}
