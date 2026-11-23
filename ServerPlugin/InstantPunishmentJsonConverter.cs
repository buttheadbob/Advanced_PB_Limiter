#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Advanced_PB_Limiter.Utils;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>
/// Reads the instant kill and memory punishments when the config is read from JSON. Before 1.3.2 they offered Damage;
/// that value is read as TurnOff instead of failing the whole config. Writes the name, as PluginSdk does. A canonical
/// cluster config must also save back to the same text, so there PluginSdk still refuses a value this reads differently.
/// A converter does not know which property it reads, so each setting has its own subclass that names it.
/// A value read as TurnOff is noted while the config is read and logged by LimiterConfig once the whole config was read,
/// so a config that fails on a later value logs nothing about it. The JSON that is read and kept is the config Quasar
/// applies at runtime (its agent sets each value on the plugin's config), long after Init.
/// </summary>
internal abstract class InstantPunishmentJsonConverter : JsonConverter<Enums.InstantPunishment>
{
    // The settings read as TurnOff in the config being read on this thread: one config is read at a time on a thread.
    [ThreadStatic] private static List<string>? _readAsTurnOff;

    private readonly string _setting;

    /// <summary>A config starts being read: anything a read that failed left behind is dropped.</summary>
    internal static void BeginConfig() => _readAsTurnOff = null;

    private static void Note(string setting) => (_readAsTurnOff ??= new List<string>()).Add(setting);

    /// <summary>The config was read whole: the settings read as TurnOff in it, or null, and forgets them.</summary>
    internal static List<string>? EndConfig()
    {
        List<string>? read = _readAsTurnOff;
        _readAsTurnOff = null;
        return read;
    }

    protected InstantPunishmentJsonConverter(string setting)
    {
        _setting = setting;
    }

    public override Enums.InstantPunishment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Enums.InstantPunishment value;
        bool wasDamage;
        bool read = reader.TokenType switch
        {
            // A number is read by the old Punishment values these settings had before 1.3.2, where 1 was Damage.
            JsonTokenType.Number => ReadNumber(ref reader, out value, out wasDamage),
            JsonTokenType.String => Enums.TryFromStoredName(reader.GetString() ?? "", out value, out wasDamage),
            _ => Refuse(out value, out wasDamage),
        };
        if (!read)
            throw new JsonException($"'{Raw(ref reader)}' is not a value for {_setting}. Use TurnOff or Destroy.");

        if (wasDamage)
            Note(reader.TokenType == JsonTokenType.Number ? InstantPunishmentFile.StoredAsOne(_setting) : _setting);
        return value;
    }

    public override void Write(Utf8JsonWriter writer, Enums.InstantPunishment value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }

    private static bool ReadNumber(ref Utf8JsonReader reader, out Enums.InstantPunishment value, out bool wasDamage)
    {
        if (reader.TryGetInt64(out long number))
            return Enums.TryFromStoredNumber(number, out value, out wasDamage);
        return Refuse(out value, out wasDamage);
    }

    private static bool Refuse(out Enums.InstantPunishment value, out bool wasDamage)
    {
        value = Enums.InstantPunishment.TurnOff;
        wasDamage = false;
        return false;
    }

    private static string Raw(ref Utf8JsonReader reader)
    {
        if (reader.HasValueSequence) return reader.TokenType.ToString();
        return Encoding.UTF8.GetString(reader.ValueSpan.ToArray());
    }
}

internal sealed class InstantKillPunishmentJsonConverter : InstantPunishmentJsonConverter
{
    public InstantKillPunishmentJsonConverter() : base(nameof(LimiterConfig.InstantKillPunishment)) { }
}

internal sealed class MemoryPunishmentJsonConverter : InstantPunishmentJsonConverter
{
    public MemoryPunishmentJsonConverter() : base(nameof(LimiterConfig.MemoryPunishment)) { }
}
