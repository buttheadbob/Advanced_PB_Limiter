#nullable enable
using System.Globalization;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// What a privileged player's run-time limit, delay, offence count or damage amount may be, in one place for every way
    /// one is entered: the CreatePrivilegedUser command, the Torch privileged players tab and Quasar profiles. NaN would
    /// never be exceeded and a negative limit always would.
    /// </summary>
    public static class Limits
    {
        /// <summary>A finite number of 0 or more.</summary>
        public static bool IsLimit(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

        /// <summary>A whole number of 0 or more: an offence count, a delay in seconds or a damage amount.</summary>
        public static bool IsCount(long value) => value >= 0;

        /// <summary>
        /// Text as a limit, read as an invariant number with a dot for decimals, as the boxes are filled. A comma is refused,
        /// as NumberStyles.Float allows no thousands separator: "1,000" could mean a thousand or one, and read either way
        /// would set a limit the admin did not mean.
        /// </summary>
        public static bool TryParse(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && IsLimit(value);
    }
}
