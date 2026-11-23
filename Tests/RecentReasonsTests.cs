using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.QuasarContracts;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class RecentReasonsTests
    {
        private static readonly long WindowMs = (long)RecentReasons.Window.TotalMilliseconds;

        [Fact]
        public void TheSameReasonIsLoggedOnceWithinTheWindow()
        {
            long now = 0;
            RecentReasons reasons = new(() => now);

            Assert.True(reasons.IsNew("profile.apply", "busy"));
            for (int i = 0; i < 20; i++)
            {
                now += 15_000; // Quasar sends it again every 15 s.
                if (now >= WindowMs) break;
                Assert.False(reasons.IsNew("profile.apply", "busy"));
            }
        }

        [Fact]
        public void TwoReasonsInTurnAreEachLoggedOnce()
        {
            long now = 0;
            RecentReasons reasons = new(() => now);
            int logged = 0;

            for (int i = 0; i < 100; i++)
            {
                now += 1_000;
                if (reasons.IsNew("report.snapshot", i % 2 == 0 ? "late" : "busy"))
                    logged++;
            }

            Assert.Equal(2, logged);
        }

        [Fact]
        public void AReasonIsLoggedAgainOnceTheWindowHasPassed()
        {
            long now = 0;
            RecentReasons reasons = new(() => now);

            Assert.True(reasons.IsNew("report.snapshot", "busy"));
            now += WindowMs - 1;
            Assert.False(reasons.IsNew("report.snapshot", "busy"));
            now++;
            Assert.True(reasons.IsNew("report.snapshot", "busy"));
        }

        [Fact]
        public void KeysAreApart()
        {
            RecentReasons reasons = new(() => 0);

            Assert.True(reasons.IsNew("report.snapshot", "busy"));
            Assert.True(reasons.IsNew("profile.apply", "busy"));
            Assert.False(reasons.IsNew("profile.apply", "busy"));
        }

        [Fact]
        public void ForgetMakesTheReasonNewAgain()
        {
            RecentReasons reasons = new(() => 0);

            Assert.True(reasons.IsNew("a", "busy"));
            reasons.Forget("a");
            Assert.True(reasons.IsNew("a", "busy"));
        }

        [Fact]
        public void OnlyTheNewestReasonsPerKeyAreKept()
        {
            RecentReasons reasons = new(() => 0);

            for (int i = 0; i <= RecentReasons.PerKey; i++)
                Assert.True(reasons.IsNew("a", Name("reason", i)));

            // The oldest was pushed out by the last one, so it is new again; the newest is still remembered.
            Assert.True(reasons.IsNew("a", Name("reason", 0)));
            Assert.False(reasons.IsNew("a", Name("reason", RecentReasons.PerKey)));
        }

        [Fact]
        public void ManyKeysNeverMissAFirstLog()
        {
            RecentReasons reasons = new(() => 0);

            for (int i = 0; i < RecentReasons.MaxKeys * 3; i++)
                Assert.True(reasons.IsNew(Name("key", i), "busy"));
        }

        [Fact]
        public void OverTheCapOnlyTheLeastRecentKeyIsForgotten()
        {
            long now = 0;
            RecentReasons reasons = new(() => now);
            for (int i = 0; i < RecentReasons.MaxKeys; i++)
            {
                now = i;
                Assert.True(reasons.IsNew(Name("key", i), "busy"));
            }

            now = RecentReasons.MaxKeys;
            Assert.True(reasons.IsNew("one more", "busy"));
            // The others keep their reasons, so a server failing alike is still logged once, not on every pass.
            Assert.False(reasons.IsNew(Name("key", 1), "busy"));
            Assert.False(reasons.IsNew(Name("key", RecentReasons.MaxKeys - 1), "busy"));
            // The least recently logged one was the one forgotten.
            Assert.True(reasons.IsNew(Name("key", 0), "busy"));
        }

        private static string Name(string prefix, int number) => prefix + " " + number.ToString(CultureInfo.InvariantCulture);

        [Fact]
        public void OfManyThreadsFailingAlikeOnlyOneLogs()
        {
            RecentReasons reasons = new(() => 0);
            int logged = 0;

            Parallel.For(0, 1000, _ =>
            {
                if (reasons.IsNew("profile.apply", "busy"))
                    Interlocked.Increment(ref logged);
            });

            Assert.Equal(1, logged);
        }
    }
}
