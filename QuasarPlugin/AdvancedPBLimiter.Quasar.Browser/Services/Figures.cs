using System.Globalization;
using Advanced_PB_Limiter.Utils;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>
/// Formats the run times and memory shown on the live page and written to the report file, so both read the same.
/// Invariant: the figures read the same whatever culture Quasar runs under.
/// </summary>
internal static class Figures
{
    /// <summary>Milliseconds to four places, for the report file.</summary>
    public static string Ms(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>Milliseconds for the pages: two places from 1 ms, three from 0.01 ms, four below that.</summary>
    public static string Time(double value)
    {
        double size = Math.Abs(value);
        string format = "0.0000";
        if (size >= 1) format = "0.00";
        else if (size >= 0.01) format = "0.000";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>A block's memory as MemoryText writes it, or a dash for a block with no memory figure yet.</summary>
    public static string Memory(long bytes, bool unmeasured) => !unmeasured && bytes <= 0 ? "-" : MemoryText.Block(bytes, unmeasured);

    /// <summary>A player's memory as MemoryText writes it, or a dash for a player with no memory figure yet.</summary>
    public static string MemoryTotal(long bytes, bool anyUnmeasured) => !anyUnmeasured && bytes <= 0 ? "-" : MemoryText.Total(bytes, anyUnmeasured);

}
