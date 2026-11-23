using ExtensionMethods;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class SafeNameTests
    {
        [Fact]
        public void AnOrdinaryNameIsKept()
        {
            const string name = "Mining Ship PB [Main] \u00C4\u00D6\u00DC \u91C7\u77FF \U0001F680";
            Assert.Same(name, name.SafeName());
        }

        [Theory]
        [InlineData("a\nb", "a b")]
        [InlineData("a\r\nb", "a  b")]
        [InlineData("a\u2028b", "a b")]
        [InlineData("a\u202Eb", "a b")] // Right-to-left override.
        [InlineData("a\u2066b\u2069", "a b ")] // Isolates.
        [InlineData("a\u200Bb\u2060c\uFEFF", "a b c ")] // Zero-width space, word joiner, BOM.
        [InlineData("a\U000E0001b", "a b")] // Language tag: one space for the surrogate pair.
        [InlineData("a\U000E0041\U000E007Fb", "a  b")] // Tag characters.
        [InlineData("a\U0001D173b", "a b")] // Musical symbol begin beam (format).
        [InlineData("a\U00013430b", "a b")] // Egyptian hieroglyph format control.
        public void UnsafeCharactersBecomeSpaces(string name, string safe)
        {
            Assert.Equal(safe, name.SafeName());
        }

        [Fact]
        public void ALoneSurrogateBecomesASpace()
        {
            // Built here: an attribute can't hold a lone surrogate.
            string name = "a" + (char)0xD800 + "b" + (char)0xDC00;
            Assert.Equal("a b ", name.SafeName());
        }

        [Fact]
        public void ACharacterTheRuntimeDoesNotKnowBecomesASpace()
        {
            Assert.Equal("a b", ("a" + char.ConvertFromUtf32(0xE0080) + "b").SafeName()); // Unassigned in every Unicode version so far.
        }

        [Fact]
        public void ALongNameIsCutToAtMostTheLimit()
        {
            string name = new('x', 150);
            Assert.Equal(new string('x', SafeNames.MaxNameLength) + "...", name.SafeName());
        }

        [Fact]
        public void ACutNeverSplitsASurrogatePair()
        {
            string name = new string('x', SafeNames.MaxNameLength - 1) + "\U0001F680tail";
            Assert.Equal(new string('x', SafeNames.MaxNameLength - 1) + "...", name.SafeName());
        }
    }
}
