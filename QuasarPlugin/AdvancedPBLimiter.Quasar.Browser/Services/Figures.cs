using System.Globalization;
using Advanced_PB_Limiter.Utils;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>
/// Formats the run times and memory shown on the live page and written to the report file, so both read the same.
/// Invariant: the figures read the same whatever culture Quasar runs under.
/// </summary>
internal static class Figures
{
    /// <summary>Milliseconds to four places.</summary>
    public static string Ms(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>A block's memory as MemoryText writes it, or a dash for a block with no memory figure yet.</summary>
    public static string Memory(long bytes, bool unmeasured) => !unmeasured && bytes <= 0 ? "-" : MemoryText.Block(bytes, unmeasured);

    /// <summary>A player's memory as MemoryText writes it, or a dash for a player with no memory figure yet.</summary>
    public static string MemoryTotal(long bytes, bool anyUnmeasured) => !anyUnmeasured && bytes <= 0 ? "-" : MemoryText.Total(bytes, anyUnmeasured);

}
