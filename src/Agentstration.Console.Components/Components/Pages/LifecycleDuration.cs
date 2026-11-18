using System.Globalization;

namespace Agentstration.Web.Components.Pages;

internal static class LifecycleDuration
{
    public static string Format(TimeSpan? value) => value is null ? string.Empty
        : $"{(long)value.Value.TotalHours:00}:{value.Value.Minutes:00}:{value.Value.Seconds:00}";
    public static bool TryParse(string text, out TimeSpan? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || hours is < 0 or > 87600 || minutes is < 0 or > 59 || seconds is < 0 or > 59) return false;
        var parsed = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        if (parsed <= TimeSpan.Zero || parsed > TimeSpan.FromDays(3650)) return false;
        value = parsed;
        return true;
    }
}
