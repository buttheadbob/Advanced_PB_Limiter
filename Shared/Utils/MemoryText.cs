#nullable enable
using System.Globalization;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Memory figures as chat, the Torch Reports tab and the Quasar pages write them, so all read the same. Invariant: the
    /// same whatever culture the host runs under. Shared with the Quasar plugin, so it uses nothing from the game.
    /// </summary>
    internal static class MemoryText
    {
        /// <summary>Shown for a block the memory check could not measure, see TrackedPBBlock.MemoryUnmeasured.</summary>
        public const string NotMeasurable = "not measurable";

        /// <summary>In KB below a megabyte and in MB from there, to two places.</summary>
        public static string Size(long bytes) => bytes >= 1024 * 1024
            ? (bytes / (1024d * 1024d)).ToString("N2", CultureInfo.InvariantCulture) + " MB"
            : (bytes / 1024d).ToString("N2", CultureInfo.InvariantCulture) + " KB";

        /// <summary>A block's memory, or <see cref="NotMeasurable"/> when the memory check could not measure it.</summary>
        public static string Block(long bytes, bool unmeasured) => unmeasured ? NotMeasurable : Size(bytes);

        /// <summary>A player's memory: the blocks that could be measured, marked when one could not, or only the mark.</summary>
        public static string Total(long bytes, bool anyUnmeasured)
        {
            if (!anyUnmeasured) return Size(bytes);
            return bytes > 0 ? Size(bytes) + " + " + NotMeasurable : NotMeasurable;
        }
    }
}
