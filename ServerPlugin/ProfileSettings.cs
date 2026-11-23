#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Advanced_PB_Limiter.Utils;
using PluginSdk.Config;

namespace Advanced_PB_Limiter.MagnetarHost;

/// <summary>A server's settings as JSON, for the profiles in Quasar.</summary>
internal static class ProfileSettings
{
    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { Converters = { new StringEnumConverter() } });

    // ShowTimingsBurp is runtime only, see LimiterConfig.
    private static readonly PropertyInfo[] Properties = typeof(LimiterConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(p => p.CanWrite && p.Name != nameof(LimiterConfig.ShowTimingsBurp))
        .ToArray();

    // The range each option offers in Quasar, read once rather than on every export and profile read.
    private static readonly Dictionary<PropertyInfo, OptionRange> Ranges = ReadRanges();

    /// <summary>The range attribute an option has, of whichever number type it is; the others are null.</summary>
    private readonly struct OptionRange
    {
        public readonly IntOptionAttribute? Int;
        public readonly LongOptionAttribute? Long;
        public readonly FloatOptionAttribute? Float;
        public readonly DoubleOptionAttribute? Double;

        public OptionRange(IntOptionAttribute? intRange, LongOptionAttribute? longRange, FloatOptionAttribute? floatRange, DoubleOptionAttribute? doubleRange)
        {
            Int = intRange;
            Long = longRange;
            Float = floatRange;
            Double = doubleRange;
        }
    }

    private static Dictionary<PropertyInfo, OptionRange> ReadRanges()
    {
        Dictionary<PropertyInfo, OptionRange> ranges = new();
        foreach (PropertyInfo property in Properties)
        {
            ranges[property] = new OptionRange(property.GetCustomAttribute<IntOptionAttribute>(), property.GetCustomAttribute<LongOptionAttribute>(),
                property.GetCustomAttribute<FloatOptionAttribute>(), property.GetCustomAttribute<DoubleOptionAttribute>());
        }
        return ranges;
    }

    public static int Count => Properties.Length;

    public static string Export(LimiterConfig config)
    {
        JObject settings = new();
        foreach (PropertyInfo property in Properties)
        {
            object value = property.GetValue(config)!;
            // A value no server would accept, set by hand in the file, is refused now, not on every server it is applied to.
            Check(property.Name, value, ServerSide);
            CheckRange(property, value, ServerSide);
            settings[property.Name] = JToken.FromObject(value, Serializer);
        }
        return JsonConvert.SerializeObject(settings);
    }

    /// <summary>
    /// Reads every value before anything is set, so a bad one leaves the server as it was. A setting the profile does
    /// not have, because it was saved by an older version, keeps the server's value. <paramref name="damage"/> names the
    /// instant punishments a profile from before 1.3.2 held as Damage, read as TurnOff.
    /// </summary>
    public static List<KeyValuePair<PropertyInfo, object>> Read(string settingsJson, out List<string> damage)
    {
        damage = new List<string>();
        JObject settings = JObject.Parse(settingsJson);
        List<KeyValuePair<PropertyInfo, object>> values = new(Properties.Length);
        foreach (PropertyInfo property in Properties)
        {
            if (settings.TryGetValue(property.Name, StringComparison.Ordinal, out JToken? token))
            {
                if (property.PropertyType == typeof(Enums.InstantPunishment))
                {
                    values.Add(new KeyValuePair<PropertyInfo, object>(property, ReadInstantPunishment(property.Name, token, damage)));
                    continue;
                }

                object value = token.ToObject(property.PropertyType, Serializer)
                               ?? throw new InvalidOperationException($"The profile has no value for {property.Name}.");
                Check(property.Name, value, ProfileSide);
                CheckRange(property, value, ProfileSide);
                values.Add(new KeyValuePair<PropertyInfo, object>(property, value));
            }
        }

        if (values.Count == 0)
            throw new InvalidOperationException("The profile has no settings this version knows.");

        return values;
    }

    // Who the message is about: a profile being applied, or this server's config being saved as one.
    private const string ProfileSide = "The profile's";
    private const string ServerSide = "This server's settings can't be saved as a profile until its config is fixed: its";

    /// <summary>
    /// Refuses what the commands refuse: a number an enum does not have, which no config save would then accept, a limit
    /// that is not a number, which would never be exceeded, and in privileged rows a negative limit, offence count, delay
    /// or damage.
    /// </summary>
    private static void Check(string name, object value, string whose)
    {
        switch (value)
        {
            case Enum enumValue when !Enum.IsDefined(enumValue.GetType(), enumValue):
                throw new InvalidOperationException($"{whose} {name} is {Convert.ToString(enumValue, CultureInfo.InvariantCulture)}, which is not a value it has.");
            case double number when double.IsNaN(number) || double.IsInfinity(number):
                throw new InvalidOperationException($"{whose} {name} is not a number.");
            case List<PrivilegedPlayerEntry> players:
                foreach (PrivilegedPlayerEntry player in players)
                {
                    string row = $"{name} row \"{player.Name}\"";
                    Check(row + " Punishment", player.Punishment, whose);
                    foreach (double limit in new[] { player.RuntimeAllowance, player.RuntimeAverageAllowance, player.StartupAllowance, player.CombinedRuntimeAllowance, player.CombinedRuntimeAverageAllowance })
                    {
                        Check(row, limit, whose); // Not a number: said as such first.
                        if (!Limits.IsLimit(limit)) throw new InvalidOperationException($"{whose} {row} has a negative limit.");
                    }
                    if (!Limits.IsCount(player.GracefulShutDownRequestDelay) || !Limits.IsCount(player.OffencesBeforePunishment) || !Limits.IsCount(player.DamageAmount))
                        throw new InvalidOperationException($"{whose} {row} has a negative delay, offence count or damage.");
                }
                break;
        }
    }

    /// <summary>A number outside the range its option offers in Quasar, which Quasar itself would not let anyone enter.</summary>
    private static void CheckRange(PropertyInfo property, object value, string whose)
    {
        OptionRange range = Ranges[property];
        bool inRange = (range.Int, range.Long, range.Float, range.Double, value) switch
            {
                ({ } io, _, _, _, int i) => i >= io.Min && i <= io.Max,
                (_, { } lo, _, _, long l) => l >= lo.Min && l <= lo.Max,
                (_, _, { } fo, _, float f) => f >= fo.Min && f <= fo.Max,
                (_, _, _, { } d, double v) => v >= d.Min && v <= d.Max,
                _ => true,
            };
        if (!inRange)
            throw new InvalidOperationException($"{whose} {property.Name} is {Convert.ToString(value, CultureInfo.InvariantCulture)}, outside the range the setting allows.");
    }

    /// <summary>A name, or a number by the old Punishment values these settings had before 1.3.2; Damage is read as TurnOff.</summary>
    private static Enums.InstantPunishment ReadInstantPunishment(string name, JToken token, List<string> damage)
    {
        Enums.InstantPunishment value = Enums.InstantPunishment.TurnOff;
        bool wasDamage = false;
        JTokenType type = token.Type;
        bool read = type switch
        {
            // A number too large for a long is held as a BigInteger, and is refused like any other unknown value.
            JTokenType.Integer => token is JValue { Value: long number } && Enums.TryFromStoredNumber(number, out value, out wasDamage),
            JTokenType.String => Enums.TryFromStoredName((string?)token ?? "", out value, out wasDamage),
            _ => false,
        };
        if (read)
        {
            if (wasDamage) damage.Add(type == JTokenType.Integer ? InstantPunishmentFile.StoredAsOne(name) : name);
            return value;
        }

        string shown = type.ToString();
        if (token is JValue plain)
            shown = plain.Value is null ? "null" : Convert.ToString(plain.Value, CultureInfo.InvariantCulture) ?? "";
        throw new InvalidOperationException($"The profile's {name} is '{shown}'. Use TurnOff or Destroy.");
    }
}
