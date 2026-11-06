using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

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

    public static int Count => Properties.Length;

    public static string Export(LimiterConfig config)
    {
        JObject settings = new();
        foreach (PropertyInfo property in Properties)
            settings[property.Name] = JToken.FromObject(property.GetValue(config)!, Serializer);
        return JsonConvert.SerializeObject(settings);
    }

    /// <summary>
    /// Reads every value before anything is set, so a bad one leaves the server as it was. A setting the profile does
    /// not have, because it was saved by an older version, keeps the server's value.
    /// </summary>
    public static List<KeyValuePair<PropertyInfo, object>> Read(string settingsJson)
    {
        JObject settings = JObject.Parse(settingsJson);
        List<KeyValuePair<PropertyInfo, object>> values = new(Properties.Length);
        foreach (PropertyInfo property in Properties)
        {
            if (settings.TryGetValue(property.Name, out JToken? token))
            {
                object value = token.ToObject(property.PropertyType, Serializer)
                               ?? throw new InvalidOperationException($"The profile has no value for {property.Name}.");
                values.Add(new KeyValuePair<PropertyInfo, object>(property, value));
            }
        }

        if (values.Count == 0)
            throw new InvalidOperationException("The profile has no settings this version knows.");

        return values;
    }
}
