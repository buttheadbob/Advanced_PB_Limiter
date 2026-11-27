using System.Linq;
using Advanced_PB_Limiter.QuasarContracts;
using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    /// <summary>
    /// The spike rule on the run patterns scripts show: one or two slow runs among the last 50 are forgiven, a pattern is not.
    /// The limits used are a single-run limit of 1 ms and an average limit of 0.5 ms.
    /// </summary>
    public class RunTimeWindowTests
    {
        private const double Limit = 1.0;
        private const double AverageLimit = 0.5;

        /// <summary>Feeds the runs as TrackedPBBlock does, and counts what the limits would do with them.</summary>
        private static (int SingleOffences, int AverageOffences, RunTimeWindow Window) Run(params double[] runs)
        {
            RunTimeWindow window = new();
            int single = 0, average = 0;
            foreach (double run in runs)
            {
                window.Add(run, 0);
                if (run > Limit && !window.IsSpikeOver(Limit)) single++;
                if (window.Average > AverageLimit) average++;
            }
            return (single, average, window);
        }

        private static readonly double[] OneSpike = { 50.0 };
        private static readonly double[] ThreeSpikes = { 50.0, 0.002, 40.0, 0.002, 30.0 };
        private static readonly double[] ShortRuns = { 1.0, 9.0, 1.0 };
        private static readonly double[] TenRuns = { 1.0, 9.0, 1.0, 1.0, 7.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
        private static readonly int[] SlowestTwo = { 1, 4 };
        private static readonly double[] EarlySpike = { 0.05, 0.05, 20.0, 0.05, 0.05 };
        private static readonly double[] EarlyPattern = { 5.0, 5.0, 5.0, 0.05 };
        private static readonly bool[] EarlySpikeMarked = { false, false, true, false, false };
        private static readonly bool[] EarlyPatternMarked = { true, true, false, false };

        private static double[] Repeat(double value, int count) => Enumerable.Repeat(value, count).ToArray();

        private static double[] Every(int period, double heavy, double light, int count) =>
            Enumerable.Range(0, count).Select(index => index % period == 0 ? heavy : light).ToArray();

        [Fact]
        public void ASingleSpikeOfASteadyScriptIsForgiven()
        {
            var (single, average, window) = Run(Repeat(0.002, 60).Concat(OneSpike).Concat(Repeat(0.002, 40)).ToArray());

            Assert.Equal(0, single);
            Assert.Equal(0, average);
            Assert.Equal(0.002, window.Average, 6);
        }

        [Fact]
        public void TwoSpikesAreForgivenAndTheThirdCounts()
        {
            var (single, _, _) = Run(Repeat(0.002, 60).Concat(ThreeSpikes).ToArray());

            Assert.Equal(1, single);
        }

        [Fact]
        public void AScriptThatTurnsHeavyCountsFromItsThirdHeavyRun()
        {
            var (single, average, window) = Run(Repeat(0.002, 60).Concat(Repeat(5.0, 40)).ToArray());

            Assert.Equal(38, single);
            Assert.True(average > 0);
            // The window still holds 10 light runs; the 2 slowest are left out: (10 x 0.002 + 38 x 5) / 48.
            Assert.Equal((10 * 0.002 + 38 * 5.0) / 48, window.Average, 6);
        }

        [Fact]
        public void HeavyWorkEveryFifthRunCountsForBothLimits()
        {
            // Before, the heavy runs stood outside the outlier fence: skipped by the run-time limit, and left out of the average.
            var (single, average, window) = Run(Every(5, 8.0, 0.01, 300));

            Assert.True(single > 50);
            Assert.True(average > 200);
            Assert.True(window.Average > AverageLimit);
        }

        [Fact]
        public void AnOccasionalSlowRunOfANoisyScriptIsForgiven()
        {
            double[] runs = Enumerable.Range(0, 300).Select(index => index % 25 == 12 ? 1.2 : 0.1 + index % 7 * 0.03).ToArray();

            var (single, average, _) = Run(runs);

            Assert.Equal(0, single);
            Assert.Equal(0, average);
        }

        [Fact]
        public void ANewHeavyBlockIsHeldToTheAverageFromItsFirstRuns()
        {
            // Before, the average was 0 until 50 runs were in.
            var (single, average, _) = Run(Repeat(5.0, 12));

            Assert.Equal(10, single);
            Assert.Equal(12, average);
        }

        [Fact]
        public void TheAverageLeavesOutTheTwoSlowestRunsOnlyOnceThereAreTen()
        {
            RunTimeWindow window = new();
            foreach (double run in new[] { 1.0, 1.0, 1.0, 10.0 })
                window.Add(run, 0);
            Assert.Equal(3.25, window.Average, 6);

            foreach (double run in Repeat(1.0, 6))
                window.Add(run, 0);
            Assert.Equal(1.0, window.Average, 6);
        }

        [Fact]
        public void ASpikeInTheFirstRunsIsLeftOutOfTheAverageTheLimitChecks()
        {
            RunTimeWindow window = new();
            foreach (double run in EarlySpike)
                window.Add(run, Limit);

            Assert.True(window.Average > AverageLimit);
            Assert.Equal(0.05, window.AverageWithoutSpikes, 6);
        }

        [Fact]
        public void WithNoLimitAOneOffSlowRunIsASpikeOnceThereAreTen()
        {
            RunTimeWindow window = new();
            foreach (double run in Repeat(0.1, 20))
                Assert.False(window.Add(run, 0));

            Assert.True(window.Add(5.0, 0));

            RunTimeWindow few = new();
            foreach (double run in Repeat(0.1, 5))
                few.Add(run, 0);
            Assert.False(few.Add(5.0, 0));
        }

        [Fact]
        public void WithNoLimitARisingRunTimeHasAtMostTwoSpikesInFifty()
        {
            RunTimeWindow window = new();
            foreach (double run in Repeat(0.1, 10))
                window.Add(run, 0);

            int spikes = 0;
            for (int run = 1; run <= RunTimeWindow.Size; run++)
            {
                if (window.Add(0.1 + run * 0.05, 0)) spikes++;
            }
            Assert.True(spikes <= RunTimeWindow.Spikes);
        }

        [Fact]
        public void WithNoLimitWidelyVaryingRunsAreNotSpikesButAHitchIs()
        {
            RunTimeWindow window = new();
            for (int run = 0; run < 200; run++)
                Assert.False(window.Add(run % 4 == 0 ? 0.25 : 0.1, 0));

            Assert.True(window.Add(5.0, 0));
        }

        [Fact]
        public void WithNoLimitRunsTiedAtTheTopAreNotSpikes()
        {
            RunTimeWindow window = new();
            foreach (double run in Repeat(2.0, 30))
                Assert.False(window.Add(run, 0));
        }

        [Fact]
        public void TheReportMarksTheRunsThatWereSpikesInTheFirstRuns()
        {
            Assert.Equal(EarlySpikeMarked, Marks(EarlySpike, Limit));
            // The first two runs over the limit were spikes when they came; the third is a pattern and was not.
            Assert.Equal(EarlyPatternMarked, Marks(EarlyPattern, Limit));
        }

        [Fact]
        public void ASpikeStaysForgivenWhenAPatternFollows()
        {
            RunTimeWindow window = new();
            bool[] spikes = EarlyPattern.Select(run => window.Add(run, Limit)).ToArray();

            Assert.Equal(EarlyPatternMarked, spikes);
            Assert.Equal((5.0 + 0.05) / 2, window.AverageWithoutSpikes, 6);
        }

        [Fact]
        public void WithNoLimitAHitchAmongVaryingRunsIsASpikeAndTheVariationIsNot()
        {
            RunTimeWindow window = new();
            for (int run = 0; run < 200; run++)
                Assert.False(window.Add(0.1 + (run % 7) * 0.004, 0));

            Assert.True(window.Add(5.0, 0));
        }

        [Fact]
        public void ALimitRaisedBetweenRunsGivesNoMoreThanTwoSpikesInFifty()
        {
            RunTimeWindow window = new();
            foreach (double run in Repeat(0.1, 20))
                window.Add(run, Limit);
            int spikes = 0;
            if (window.Add(5.0, Limit)) spikes++;
            if (window.Add(5.0, Limit)) spikes++;
            if (window.Add(12.0, 10.0)) spikes++;
            if (window.Add(12.0, 10.0)) spikes++;

            Assert.Equal(RunTimeWindow.Spikes, spikes);
        }

        [Fact]
        public void EachRunKeepsWhatBecameOfIt()
        {
            RunTimeWindow window = new();
            foreach (double run in Repeat(0.1, 20))
                window.Add(run, Limit);
            window.Add(5.0, Limit);   // over, forgiven
            window.Add(5.0, Limit);   // over, forgiven
            window.Add(5.0, Limit);   // over, a pattern: not checked until the punishment check counts it
            window.Add(5.0, Limit);
            window.MarkLast(RunOutcome.Counted);

            RunOutcome[] outcomes = window.CopyOutcomesInRunOrder();
            Assert.Equal(RunOutcome.Under, outcomes[0]);
            Assert.Equal(RunOutcome.Forgiven, outcomes[20]);
            Assert.Equal(RunOutcome.Forgiven, outcomes[21]);
            Assert.Equal(RunOutcome.NotChecked, outcomes[22]);
            Assert.Equal(RunOutcome.Counted, outcomes[23]);
        }

        [Fact]
        public void ASpikeThatIsTheInstantKillGivesBackItsPlace()
        {
            RunTimeWindow window = new();
            foreach (double run in Repeat(0.1, 20))
                window.Add(run, Limit);
            Assert.True(window.Add(5.0, Limit));
            window.MarkLast(RunOutcome.InstantKilled);
            Assert.Equal(RunOutcome.InstantKilled, window.CopyOutcomesInRunOrder()[20]);

            // Its spike place is free again, so the next run over the limit is forgiven; the one after is the third over the
            // limit in the window, a pattern, and is not.
            Assert.True(window.Add(5.0, Limit));
            Assert.False(window.Add(5.0, Limit));
        }

        [Fact]
        public void AnInstantKillWithNoLimitIsKeptAsSuch()
        {
            RunTimeWindow window = new();
            foreach (double run in Repeat(0.1, 20))
                window.Add(run, 0);
            window.Add(20.0, 0);
            window.MarkLast(RunOutcome.InstantKilled);

            RunOutcome[] outcomes = window.CopyOutcomesInRunOrder();
            Assert.Equal(RunOutcome.InstantKilled, outcomes[outcomes.Length - 1]);
            Assert.DoesNotContain(RunOutcome.StoodOut, outcomes);
        }

        [Fact]
        public void WithNoLimitASpikeIsKeptAsStoodOut()
        {
            RunTimeWindow window = new();
            foreach (double run in Repeat(0.1, 20))
                window.Add(run, 0);
            window.Add(5.0, 0);

            RunOutcome[] outcomes = window.CopyOutcomesInRunOrder();
            Assert.Equal(RunOutcome.StoodOut, outcomes[outcomes.Length - 1]);
        }

        private static bool[] Marks(double[] runs, double limit)
        {
            RunTimeWindow window = new();
            foreach (double run in runs)
                window.Add(run, limit);
            return window.CopyLeftOutInRunOrder();
        }

        [Fact]
        public void TheWindowKeepsTheLastFiftyRuns()
        {
            RunTimeWindow window = new();
            for (int run = 1; run <= 60; run++)
                window.Add(run, 0);

            double[] runs = window.CopyInRunOrder();
            Assert.Equal(RunTimeWindow.Size, runs.Length);
            Assert.Equal(11, runs[0]);
            Assert.Equal(60, runs[runs.Length - 1]);
            Assert.Equal(10, window.CountAbove(50));
            Assert.Equal(0, window.CountAbove(60));
        }

        [Fact]
        public void TheReportMarksTheTwoSlowestRunsOnceThereAreTen()
        {
            Assert.DoesNotContain(true, Marks(ShortRuns, 0));

            bool[] leftOut = Marks(TenRuns, 0);
            Assert.Equal(SlowestTwo, leftOut.Select((flag, index) => (flag, index)).Where(x => x.flag).Select(x => x.index));
        }

        [Fact]
        public void ClearEmptiesTheWindow()
        {
            RunTimeWindow window = new();
            window.Add(3, 0);
            window.Clear();

            Assert.Equal(0, window.Count);
            Assert.Equal(0, window.Average);
            Assert.Empty(window.CopyInRunOrder());
        }
    }

    /// <summary>The instant kill comes on the second run over its threshold within the window, so a single spike passes.</summary>
    public class InstantKillStrikesTests
    {
        private const long Second = 1000;

        [Fact]
        public void ASingleRunOverTheThresholdDoesNotKill()
        {
            InstantKillStrikes strikes = new();

            Assert.False(strikes.Strike(0, Second, 2, 60));
            Assert.Equal(1, strikes.Count);
        }

        [Fact]
        public void ASecondRunWithinTheWindowKills()
        {
            InstantKillStrikes strikes = new();
            strikes.Strike(0, Second, 2, 60);

            Assert.True(strikes.Strike(59 * Second, Second, 2, 60));
            Assert.Equal(0, strikes.Count);
        }

        [Fact]
        public void ASecondRunAfterTheWindowStartsAgain()
        {
            InstantKillStrikes strikes = new();
            strikes.Strike(0, Second, 2, 60);

            Assert.False(strikes.Strike(61 * Second, Second, 2, 60));
            Assert.True(strikes.Strike(62 * Second, Second, 2, 60));
        }

        [Fact]
        public void OneStrikeKillsAtOnceAsBefore()
        {
            Assert.True(new InstantKillStrikes().Strike(0, Second, 1, 60));
        }

        [Fact]
        public void AWindowUnderASecondStillLetsStrikesMeet()
        {
            InstantKillStrikes strikes = new();
            strikes.Strike(0, Second, 2, 0);

            Assert.True(strikes.Strike(Second / 2, Second, 2, 0));
        }

        [Fact]
        public void ThreeStrikesNeedThree()
        {
            InstantKillStrikes strikes = new();

            Assert.False(strikes.Strike(0, Second, 3, 60));
            Assert.False(strikes.Strike(Second, Second, 3, 60));
            Assert.True(strikes.Strike(2 * Second, Second, 3, 60));
        }
    }
}
