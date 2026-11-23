#nullable enable
using System;
using System.Globalization;
using System.IO;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Sets a file aside under a name of its own: SafeFile for the copies it keeps, and the hosts and the Quasar
    /// plugin for a file that can't be read. A class of its own, so the Quasar plugin can compile it without SafeFile.
    /// </summary>
    public static class KeptFile
    {
        /// <summary>
        /// Moves a file to its name plus <paramref name="suffix"/> in one rename, which needs no space and leaves no
        /// half-written file, and returns the new name. A name already taken gets the time, and a number when that is taken
        /// too, so an earlier file kept there is never in the way. Throws as File.Move does when the file can't be moved.
        /// </summary>
        public static string Keep(string temp, string suffix)
        {
            string kept = temp + suffix;
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            int attempt = 0;
            while (File.Exists(kept) || Directory.Exists(kept))
            {
                attempt++;
                kept = temp + "." + stamp + (attempt == 1 ? "" : "-" + attempt.ToString(CultureInfo.InvariantCulture)) + suffix;
            }
            File.Move(temp, kept);
            return kept;
        }
    }
}
