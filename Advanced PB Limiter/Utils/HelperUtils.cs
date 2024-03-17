using System.Globalization;

namespace Advanced_PB_Limiter.Utils
{
    public static class HelperUtils
    {
        public static string FormatBytesToKB(long bytes)
        {
            float kilobytes = bytes / 1024f;
            string formattedKilobytes = kilobytes.ToString("N0", CultureInfo.InvariantCulture);

            return $"{formattedKilobytes} KB";
        }
    }
}