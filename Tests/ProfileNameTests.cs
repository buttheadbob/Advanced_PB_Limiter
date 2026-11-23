using ExtensionMethods;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class ProfileNameTests
    {
        [Theory]
        [InlineData("Main")]
        [InlineData("Survival [EU]")]
        [InlineData("Mining \u00C4\u00D6\u00DC \u91C7\u77FF")]
        public void AnOrdinaryNameIsAllowed(string name)
        {
            Assert.True(name.IsSafeProfileName());
        }

        [Fact]
        public void TheLongestAllowedNameIsAllowed()
        {
            Assert.True(new string('x', SafeNames.MaxProfileNameLength).IsSafeProfileName());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void AnEmptyNameIsNotAllowed(string name)
        {
            Assert.False(name.IsSafeProfileName());
        }

        [Theory]
        [InlineData("a\nb")]
        [InlineData("a\r\nb")]
        [InlineData("a\u2028b")] // Line separator.
        [InlineData("a\u202Eb")] // Right-to-left override.
        [InlineData("a\u200Bb")] // Zero-width space.
        [InlineData("a\U000E0041b")] // Tag character.
        public void ANameWithLineBreaksOrHiddenCharactersIsNotAllowed(string name)
        {
            Assert.False(name.IsSafeProfileName());
        }

        [Fact]
        public void ANameOneCharacterTooLongIsNotAllowed()
        {
            Assert.False(new string('x', SafeNames.MaxProfileNameLength + 1).IsSafeProfileName());
        }

        [Fact]
        public void ALoneSurrogateIsNotAllowed()
        {
            string name = "a" + (char)0xD800 + "b";
            Assert.False(name.IsSafeProfileName());
        }
    }
}
