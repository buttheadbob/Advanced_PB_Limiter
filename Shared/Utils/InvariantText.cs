#nullable enable
using System;
using System.Globalization;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Values as text that reads the same on every server culture. The shared code builds for .NET Framework as well,
    /// which has no string.Create with a format provider, so messages are joined from these instead.
    /// </summary>
    internal static class InvariantText
    {
        public static string Of(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>A count with its noun, singular for 1: "1 block", "3 blocks".</summary>
        public static string Count(long count, string one, string many) => Of(count) + " " + (count == 1 ? one : many);
        public static string Of(long value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(ulong value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(double value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
        public static string Of(bool value) => value ? "True" : "False";
    }
}
