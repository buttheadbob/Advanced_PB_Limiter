using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// The instant kill and memory punishments offered Damage before 1.3.2. A config file that still holds it would fail
    /// to load as a whole, because the serializers reject a value the enum no longer has, so it is rewritten first.
    /// </summary>
    public static class InstantPunishmentFile
    {
        public static readonly string[] Settings = { "InstantKillPunishment", "MemoryPunishment" };

        /// <summary>Rewrites Damage in those two settings to TurnOff in the file, and returns the settings it changed.</summary>
        public static List<string> ReplaceDamage(string path)
        {
            List<string> changed = new();
            if (!File.Exists(path)) return changed;

            byte[] original = File.ReadAllBytes(path);
            XmlDocument document = new() { PreserveWhitespace = true };
            using (MemoryStream read = new(original))
                document.Load(read);
            XmlElement? root = document.DocumentElement;
            if (root is null) return changed;

            foreach (string name in Settings)
            {
                XmlElement? element = root[name];
                if (element is null || element.InnerText.Trim() != "Damage") continue;
                element.InnerText = "TurnOff";
                changed.Add(name);
            }

            if (changed.Count > 0)
            {
                // Written back as it was, a BOM only if it had one, so the file changes by these words alone.
                bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
                XmlWriterSettings settings = new() { Encoding = new UTF8Encoding(bom), Indent = false, NewLineHandling = NewLineHandling.None };
                using MemoryStream written = new();
                using (XmlWriter writer = XmlWriter.Create(written, settings))
                    document.Save(writer);
                File.WriteAllBytes(path, written.ToArray());
            }
            return changed;
        }

        public static string LogLine(IEnumerable<string> settings)
        {
            return $"The config held Damage for {string.Join(" and ", settings)}, which an instant kill no longer offers. Read as TurnOff, the default; set Destroy again if that was meant.";
        }
    }
}
