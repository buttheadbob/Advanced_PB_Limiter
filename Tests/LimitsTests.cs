using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class LimitsTests
    {
        [Theory]
        [InlineData("0", 0)]
        [InlineData("0.5", 0.5)]
        [InlineData("12.25", 12.25)]
        public void ANumberWithADotIsALimit(string text, double expected)
        {
            Assert.True(Limits.TryParse(text, out double value));
            Assert.Equal(expected, value);
        }

        [Theory]
        [InlineData("1,000")] // A thousand, or one: refused rather than guessed.
        [InlineData("0,5")]
        [InlineData("-1")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("")]
        public void AnythingElseIsRefused(string text)
        {
            Assert.False(Limits.TryParse(text, out _));
        }

        [Fact]
        public void ACountIsZeroOrMore()
        {
            Assert.True(Limits.IsCount(0));
            Assert.False(Limits.IsCount(-1));
        }
    }
}
