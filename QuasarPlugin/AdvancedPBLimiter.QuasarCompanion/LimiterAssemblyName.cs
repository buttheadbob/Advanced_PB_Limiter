#nullable enable
using System;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Tells the Magnetar plugin's assembly by its name, for the Quasar companion, which looks for the bridge in it and in
    /// nothing else: a script compiles into an assembly of its own, and could declare a type of the bridge's name to be
    /// called in its place. Uses nothing from the game.
    /// </summary>
    internal static class LimiterAssemblyName
    {
        // The name the project builds, which a DLL in Magnetar's Local folder keeps.
        private const string Built = "AdvancedPBLimiter";

        // A hub install is compiled by Magnetar from source and named after the plugin's FriendlyName, then "-" and a hash
        // and "_" and a random part. A script's assembly is named "<EntityId>-<CustomName>", which starts with a digit.
        private const string Compiled = "Advanced PB Limiter";

        public static bool Is(string? name)
        {
            if (name is null) return false;
            if (string.Equals(name, Built, StringComparison.Ordinal)) return true;
            return name.Length > Compiled.Length
                   && name.StartsWith(Compiled, StringComparison.Ordinal)
                   && (name[Compiled.Length] == '-' || name[Compiled.Length] == '_');
        }
    }
}
