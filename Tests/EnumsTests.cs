using System;
using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class EnumsTests
    {
        private static readonly string[] InstantNames = { "TurnOff", "Destroy" };
        private static readonly string[] OrdinaryNames = { "TurnOff", "Damage", "Destroy" };

        [Fact]
        public void InstantPunishmentHasNoDamage()
        {
            Assert.Equal(InstantNames, System.Enum.GetNames(typeof(Enums.InstantPunishment)), StringComparer.Ordinal);
        }

        [Fact]
        public void OrdinaryPunishmentKeepsDamage()
        {
            Assert.Equal(OrdinaryNames, System.Enum.GetNames(typeof(Enums.Punishment)), StringComparer.Ordinal);
        }

        [Theory]
        [InlineData(Enums.InstantPunishment.TurnOff, Enums.Punishment.TurnOff)]
        [InlineData(Enums.InstantPunishment.Destroy, Enums.Punishment.Destroy)]
        public void ToPunishmentNeverGivesDamage(Enums.InstantPunishment instant, Enums.Punishment expected)
        {
            Assert.Equal(expected, instant.ToPunishment());
        }

        [Theory]
        [InlineData(0L, Enums.InstantPunishment.TurnOff, false)]
        [InlineData(1L, Enums.InstantPunishment.TurnOff, true)]
        [InlineData(2L, Enums.InstantPunishment.Destroy, false)]
        public void NumbersAreReadByTheOldPunishmentValues(long number, Enums.InstantPunishment expected, bool expectedDamage)
        {
            Assert.True(Enums.TryFromStoredNumber(number, out Enums.InstantPunishment punishment, out bool wasDamage));
            Assert.Equal(expected, punishment);
            Assert.Equal(expectedDamage, wasDamage);
        }

        [Theory]
        [InlineData(-1L)]
        [InlineData(3L)]
        [InlineData(long.MaxValue)]
        public void OtherNumbersAreRefused(long number)
        {
            Assert.False(Enums.TryFromStoredNumber(number, out _, out _));
        }

        [Theory]
        [InlineData("TurnOff", Enums.InstantPunishment.TurnOff, false)]
        [InlineData("Destroy", Enums.InstantPunishment.Destroy, false)]
        [InlineData("destroy", Enums.InstantPunishment.Destroy, false)]
        [InlineData("TURNOFF", Enums.InstantPunishment.TurnOff, false)]
        [InlineData("Damage", Enums.InstantPunishment.TurnOff, true)]
        [InlineData("damage", Enums.InstantPunishment.TurnOff, true)]
        [InlineData(" Destroy ", Enums.InstantPunishment.Destroy, false)]
        public void NamesAreReadInAnyCase(string name, Enums.InstantPunishment expected, bool expectedDamage)
        {
            Assert.True(Enums.TryFromStoredName(name, out Enums.InstantPunishment punishment, out bool wasDamage));
            Assert.Equal(expected, punishment);
            Assert.Equal(expectedDamage, wasDamage);
        }

        [Theory]
        [InlineData("")]
        [InlineData("1")]
        [InlineData("0")]
        [InlineData("TurnOff, Destroy")]
        [InlineData(" 1 ")]
        [InlineData("Explode")]
        public void NumbersAsTextAndOtherNamesAreRefused(string name)
        {
            Assert.False(Enums.TryFromStoredName(name, out _, out _));
        }
    }
}
