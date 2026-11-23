#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

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
        public static List<string> ReplaceDamage(string path) => ReplaceDamage(path, out _);

        /// <summary>
        /// As <see cref="ReplaceDamage(string)"/>. <paramref name="recovered"/> is a line for the log when a rewrite at an
        /// earlier start was interrupted and its copy was used or kept, otherwise null. The check and the rewrite are one step
        /// for other writers of the file in this process.
        /// <para>
        /// The Torch host uses this overload and does not handle <c>copyWaiting</c>: when a complete differing copy cannot be
        /// kept apart, the config itself is moved aside as <c>.unreadable</c>, so a loader that reads this path finds it
        /// missing beside the copy and saves nothing over it. The copy is restored by the next start. Magnetar uses the
        /// three-argument overload and blocks its own saves instead.
        /// </para>
        /// </summary>
        public static List<string> ReplaceDamage(string path, out string? recovered)
        {
            using (SafeFile.Hold(path))
            {
                List<string> changed = ReplaceDamageHeld(path, out recovered, out bool copyWaiting);
                if (!copyWaiting || !File.Exists(path)) return changed;

                try
                {
                    string aside = KeptFile.Keep(path, ".unreadable");
                    recovered = $"The config {path} held Damage and a complete copy waits at {CopyPath(path)}. The config was moved aside to {aside}, and the copy is used at the next start; nothing is saved until then.";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    recovered = $"The config {path} held Damage and a complete copy waits at {CopyPath(path)}, but the config could not be moved aside ({e.Message}). The copy is used at the next start once the config is out of the way; nothing may be saved until then.";
                }
                return changed;
            }
        }

        /// <summary>
        /// As <see cref="ReplaceDamage(string, out string?)"/>. <paramref name="copyWaiting"/> is true when a differing complete
        /// copy from an interrupted write could not be kept apart and waits to be used at the next start: the config then
        /// still holds Damage, and nothing may be saved over it before that restart, or the copy would be kept aside instead.
        /// </summary>
        public static List<string> ReplaceDamage(string path, out string? recovered, out bool copyWaiting)
        {
            using (SafeFile.Hold(path))
                return ReplaceDamageHeld(path, out recovered, out copyWaiting);
        }

        /// <summary>Where the rewrite writes its complete copy before it swaps it in: the config's name plus .damage.tmp.</summary>
        public static string CopyPath(string path) => path + ".damage.tmp";

        private static List<string> ReplaceDamageHeld(string path, out string? recovered, out bool copyWaiting)
        {
            copyWaiting = false;
            List<string> changed = new();
            string temp = CopyPath(path);
            SafeFile.Recovery recovery = SafeFile.RecoverHeld(path, temp, SafeFile.IsWholeXml, out string? kept);
            recovered = SafeFile.Describe(recovery, path, temp, kept);
            if (!File.Exists(path)) return changed;

            byte[] original = File.ReadAllBytes(path);
            XDocument document;
            using (MemoryStream read = new(original))
            using (XmlReader reader = SafeXml.Reader(read))
                document = XDocument.Load(reader);
            XElement? root = document.Root;
            if (root is null) return changed;

            foreach (string name in Settings)
            {
                XElement? element = root.Element(root.Name.Namespace + name);
                if (element is not null && element.Value.Trim() == "Damage")
                    changed.Add(name);
            }
            if (changed.Count == 0) return changed;

            // Only these values change where it can be done on the text: line endings, the declaration, comments and the BOM
            // stay exactly as they were. A file in another encoding, or a value the text edit can't reach (CDATA, a
            // character reference), is rewritten from the parsed document in its own encoding instead.
            byte[]? edited = EditText(original, changed);
            if (edited is not null && !OnlyTheseChanged(edited, document, changed)) edited = null;
            using MemoryStream written = edited is not null ? new(edited) : Rewrite(original, changed);
            // A kept copy that matches this rewrite was this same rewrite, interrupted before it was swapped in.
            string? leftover = recovery switch
            {
                SafeFile.Recovery.CopyKept => kept,
                SafeFile.Recovery.KeepFailed => temp,
                _ => null
            };
            if (leftover is not null && SafeFile.DropIfSame(leftover, written.ToArray()))
            {
                recovered = null;
            }
            else if (recovery == SafeFile.Recovery.KeepFailed)
            {
                // The rewrite would write its copy over that one, which may be the admin's only copy of newer settings.
                recovered = $"A complete copy of {path} from an interrupted write differs from the config and is at {temp}, and could not be moved to a name of its own ({kept}). "
                            + $"The config, which holds Damage for {string.Join(" and ", changed)}, was not rewritten so that copy is not replaced, so it can't be read and is set aside as unreadable. "
                            + "At the next start that copy is used, unless it is moved elsewhere first, as long as nothing is saved over it until then.";
                copyWaiting = true;
                changed.Clear();
                return changed;
            }
            // Written either way; the swap error only says the file was written in place, which needs no line in the log.
            _ = SafeFile.WriteHeld(path, temp, written, SafeFile.IsWholeXml, out string? keptBeforeWrite);
            if (keptBeforeWrite is not null)
                recovered = (recovered is null ? "" : recovered + " ") + $"A complete copy an earlier write left at {temp} was kept as {keptBeforeWrite}.";
            return changed;
        }

        /// <summary>The file with Damage replaced in its UTF-8 text, or null when that can't be done on the text.</summary>
        private static byte[]? EditText(byte[] original, List<string> settings)
        {
            bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
            }
            catch (DecoderFallbackException)
            {
                return null; // Not UTF-8.
            }
            foreach (char c in text)
            {
                if (c == '\0') return null; // UTF-16 or UTF-32 without a BOM.
            }

            foreach (string name in settings)
            {
                string replaced = ReplaceValue(text, name);
                if (ReferenceEquals(replaced, text)) return null;
                text = replaced;
            }

            byte[] body = new UTF8Encoding(false).GetBytes(text);
            byte[] bytes = new byte[body.Length + (bom ? 3 : 0)];
            if (bom) Buffer.BlockCopy(original, 0, bytes, 0, 3);
            Buffer.BlockCopy(body, 0, bytes, bom ? 3 : 0, body.Length);
            return bytes;
        }

        /// <summary>
        /// True when the edited text parses to the original document with only the root's own settings set to TurnOff.
        /// The text edit takes the first element by that name, which in an unusual file could be a different one.
        /// </summary>
        private static bool OnlyTheseChanged(byte[] edited, XDocument original, List<string> settings)
        {
            XDocument expected = new(original);
            XElement root = expected.Root!;
            foreach (string name in settings)
                root.Element(root.Name.Namespace + name)?.SetValue("TurnOff");
            try
            {
                return XNode.DeepEquals(expected.Root, Parse(edited)?.Root);
            }
            catch (XmlException)
            {
                return false;
            }
        }

        private static XDocument? Parse(byte[] bytes)
        {
            using MemoryStream read = new(bytes);
            using XmlReader reader = SafeXml.Reader(read);
            return XDocument.Load(reader);
        }

        /// <summary>The file written again from its parsed document with Damage replaced, in the encoding it was read in.</summary>
        private static MemoryStream Rewrite(byte[] original, List<string> settings)
        {
            XDocument document;
            using (MemoryStream read = new(original))
            using (XmlReader reader = SafeXml.Reader(read))
                document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);

            XElement? root = document.Root;
            if (root is not null)
            {
                foreach (string name in settings)
                    root.Element(root.Name.Namespace + name)?.SetValue("TurnOff");
            }

            MemoryStream written = new();
            XmlWriterSettings writeSettings = new() { Encoding = EncodingOf(original, document), Indent = false, NewLineHandling = NewLineHandling.Entitize };
            using (XmlWriter writer = XmlWriter.Create(written, writeSettings))
                document.Save(writer);
            written.Position = 0;
            return written;
        }

        /// <summary>
        /// The encoding the file was written in: its byte order mark, else its declaration, else UTF-8. A declared encoding
        /// this runtime does not have could not have been read either, so that case does not reach here.
        /// </summary>
        private static Encoding EncodingOf(byte[] original, XDocument document)
        {
            if (original.Length >= 4 && original[0] == 0xFF && original[1] == 0xFE && original[2] == 0 && original[3] == 0) return new UTF32Encoding(false, true);
            if (original.Length >= 2 && original[0] == 0xFF && original[1] == 0xFE) return new UnicodeEncoding(false, true);
            if (original.Length >= 2 && original[0] == 0xFE && original[1] == 0xFF) return new UnicodeEncoding(true, true);
            bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
            string? declared = document.Declaration?.Encoding;
            if (bom || string.IsNullOrEmpty(declared) || declared!.StartsWith("utf-8", StringComparison.OrdinalIgnoreCase))
                return new UTF8Encoding(bom);
            if (declared.StartsWith("utf-16", StringComparison.OrdinalIgnoreCase))
                return new UnicodeEncoding(false, false);
            try
            {
                return Encoding.GetEncoding(declared, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
                return new UTF8Encoding(false);
            }
        }

        /// <summary>
        /// The value of the first element by this name that is not inside a comment, Damage with any spaces around it,
        /// replaced by TurnOff alone: the serializers read an enum without trimming it.
        /// </summary>
        private static string ReplaceValue(string text, string name)
        {
            string open = "<" + name;
            string close = "</" + name;
            for (int at = text.IndexOf(open, StringComparison.Ordinal); at >= 0; at = text.IndexOf(open, at + 1, StringComparison.Ordinal))
            {
                int afterName = at + open.Length;
                if (afterName >= text.Length || (text[afterName] != '>' && !char.IsWhiteSpace(text[afterName]))) continue;
                if (InsideComment(text, at)) continue;

                int value = text.IndexOf('>', afterName) + 1;
                if (value == 0) return text;
                int word = SkipSpace(text, value);
                if (string.CompareOrdinal(text, word, "Damage", 0, 6) != 0) continue;
                int end = SkipSpace(text, word + 6);
                if (string.CompareOrdinal(text, end, close, 0, close.Length) != 0) continue;

                return text.Substring(0, value) + "TurnOff" + text.Substring(end);
            }
            return text;
        }

        private static int SkipSpace(string text, int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            return index;
        }

        /// <summary>True when the last comment opened before this point has not been closed by it.</summary>
        private static bool InsideComment(string text, int index)
        {
            int opened = text.LastIndexOf("<!--", index, StringComparison.Ordinal);
            if (opened < 0) return false;
            int closed = text.IndexOf("-->", opened, StringComparison.Ordinal);
            return closed < 0 || closed > index;
        }


        /// <summary>How the log names a setting that held the number 1, which 1.3.2 read as Destroy and older versions as Damage.</summary>
        public static string StoredAsOne(string setting)
        {
            return setting + " (stored as the number 1, which was Damage before 1.3.2 and Destroy in 1.3.2)";
        }

        public static string LogLine(IEnumerable<string> settings)
        {
            return $"The config held Damage for {string.Join(" and ", settings)}, which an instant kill no longer offers. Read as TurnOff, the default; set Destroy again if that was meant.";
        }
    }
}
