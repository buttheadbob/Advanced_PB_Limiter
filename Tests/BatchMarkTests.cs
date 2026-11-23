using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    /// <summary>
    /// The guard PunishmentManager.CheckForPunishment uses so a block is instant-killed once for runs queued in one tick.
    /// It is decided by the batch alone, so a script that turns its own block off after every run (and is turned on and run
    /// again by a timer or another block) is judged on every run, as it does not consult whether the block works.
    /// </summary>
    public sealed class BatchMarkTests
    {
        [Fact]
        public void ABlockIsJudgedUntilItIsKilledInABatch()
        {
            BatchMark kill = new();
            Assert.False(kill.In(1));
            Assert.False(kill.In(2));
        }

        [Fact]
        public void ALaterRunInTheSameBatchIsNotKilledAgain()
        {
            BatchMark kill = new();
            kill.Set(7);
            Assert.True(kill.In(7));
        }

        [Fact]
        public void ASelfDisablingScriptIsJudgedAgainInEveryLaterBatch()
        {
            BatchMark kill = new();
            // Each tick: the script runs, turns its block off, and is processed the next tick while its block is off.
            for (long batch = 1; batch <= 10; batch++)
            {
                Assert.False(kill.In(batch)); // Judged although its block is not working now.
                kill.Set(batch); // Killed for its run over the limit.
                Assert.True(kill.In(batch)); // A second run in that batch is not killed again.
            }
        }
    }
}
