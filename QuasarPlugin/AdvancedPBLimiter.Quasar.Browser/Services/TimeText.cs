using System.Globalization;

namespace AdvancedPBLimiter.Quasar.Services;

/// <summary>Small text helpers for the dashboard, kept invariant like the rest of the plugin's figures.</summary>
public static class TimeText
{
    public static string Ago(DateTime utc)
    {
        TimeSpan age = DateTime.UtcNow - utc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        if (age < TimeSpan.FromSeconds(5)) return "just now";
        if (age < TimeSpan.FromMinutes(1)) return ((int)age.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s ago";
        if (age < TimeSpan.FromHours(1)) return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m ago";
        if (age < TimeSpan.FromDays(1)) return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + "h ago";
        return ((int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + "d ago";
    }

    public static string Utc(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
