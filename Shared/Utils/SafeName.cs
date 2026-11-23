using System;
using System.Globalization;
using System.Text;

namespace ExtensionMethods
{
    /// <summary>Names made safe to write out, see <see cref="SafeName"/>.</summary>
    public static class SafeNames
    {
        /// <summary>The longest name written out; a longer one is cut and ends in "...".</summary>
        public const int MaxNameLength = 100;

        /// <summary>The most characters a limiter profile name may have.</summary>
        public const int MaxProfileNameLength = 64;

        /// <summary>
        /// A profile name an admin entered: 1 to <see cref="MaxProfileNameLength"/> characters, with no control
        /// characters, line breaks, format characters or unknown characters. <see cref="SafeName"/> would change such a
        /// name, so equality with its safe form is the test. The UI checks this, and the worker checks it again.
        /// </summary>
        public static bool IsSafeProfileName(this string text)
        {
            return !string.IsNullOrWhiteSpace(text)
                   && text.Length <= MaxProfileNameLength
                   && string.Equals(text, text.SafeName(), StringComparison.Ordinal);
        }

        /// <summary>
        /// A name a script or player chose (a block, grid or player name), made safe to write into logs, chat and reports:
        /// control characters, line breaks and format characters become spaces, so a name can't start a forged line or make
        /// one read differently, and a longer name is cut to <see cref="MaxNameLength"/> characters followed by "...".
        /// Allocates only when it changes something.
        /// </summary>
        public static string SafeName(this string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            bool safe = text.Length <= MaxNameLength;
            for (int index = 0; safe && index < text.Length; index += Width(text, index))
                safe = !Unsafe(text, index);
            if (safe) return text;

            int length = Math.Min(text.Length, MaxNameLength);
            if (length < text.Length && char.IsHighSurrogate(text[length - 1]))
                length--; // Not half a character.
            StringBuilder sb = new(length + 3);
            for (int index = 0; index < length; index += Width(text, index))
            {
                if (Unsafe(text, index))
                    sb.Append(' '); // One space for the character, also for a pair of surrogates.
                else
                    sb.Append(text, index, Width(text, index));
            }
            if (length < text.Length)
                sb.Append("...");
            return sb.ToString();
        }

        /// <summary>The UTF-16 units of the character at <paramref name="index"/>: 2 for a surrogate pair, else 1.</summary>
        private static int Width(string text, int index) => char.IsSurrogatePair(text, index) ? 2 : 1;

        /// <summary>
        /// The character at <paramref name="index"/>, a whole code point, is unsafe in a name: a control character, a line
        /// break, a format character (bidi overrides and isolates, zero-width characters, the BOM, tag characters and the
        /// other format characters outside the basic plane), which can make a line read differently from what it holds, a lone
        /// surrogate, or a character this runtime's Unicode tables do not know, which may be a format character added later
        /// (the .NET Framework that Torch and MagnetarLegacy run on has older tables than .NET 10).
        /// </summary>
        private static bool Unsafe(string text, int index)
        {
            char c = text[index];
            if (char.IsControl(c)) return true;
            if (char.IsSurrogate(c) && !char.IsSurrogatePair(text, index)) return true;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, index);
            return category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator
                   || category == UnicodeCategory.Format || category == UnicodeCategory.OtherNotAssigned;
        }
    }
}
