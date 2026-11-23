#nullable enable
using System.IO;
using System.Xml;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Readers for the plugin's own XML files, with DTDs refused and no resolver, so a file can't pull in other files or
    /// expand entities without end (on .NET Framework, where the game and Torch run, the defaults allow both). The one place
    /// these settings are made, for every file the plugin reads as XML.
    /// </summary>
    public static class SafeXml
    {
        private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

        public static XmlReader Reader(Stream stream) => XmlReader.Create(stream, Settings);

        public static XmlReader Reader(string path) => XmlReader.Create(path, Settings);
    }
}
