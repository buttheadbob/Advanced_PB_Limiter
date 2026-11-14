using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Advanced_PB_Limiter.Utils;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>
/// Reads the instant kill and memory punishments from the JSON a Quasar-managed server is given. Before 1.3.2 they
/// offered Damage; that value is read as TurnOff instead of failing the whole config. Writes the name, as PluginSdk does.
/// </summary>
internal sealed class InstantPunishmentJsonConverter : JsonConverter<Enums.InstantPunishment>
{
    /// <summary>Set when a Damage was read, so the plugin can say so once after loading.</summary>
    internal static bool ReadDamage { get; set; }

    public override Enums.InstantPunishment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number) && Enum.IsDefined(typeof(Enums.InstantPunishment), number))
            return (Enums.InstantPunishment)number;

        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (text is not null && Enum.TryParse(text, ignoreCase: false, out Enums.InstantPunishment value) && Enum.IsDefined(typeof(Enums.InstantPunishment), value))
            return value;

        if (string.Equals(text, "Damage", StringComparison.Ordinal))
        {
            ReadDamage = true;
            return Enums.InstantPunishment.TurnOff;
        }

        throw new JsonException($"'{text}' is not an instant kill punishment. Use TurnOff or Destroy.");
    }

    public override void Write(Utf8JsonWriter writer, Enums.InstantPunishment value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
