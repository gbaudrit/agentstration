using Agentstration.Management.Abstractions;

namespace Agentstration.Web.Components.Pages;

public static class TriggerUi
{
    public static string ScheduleLabel(TriggerSchedule? value) => value is null ? "—" : value.Type switch
    {
        TriggerScheduleType.Cron => $"{value.Expression} · {value.TimeZone}",
        TriggerScheduleType.Interval => $"Every {value.Every} · from {FormatDate(value.StartAt)}",
        _ => $"Once · {FormatDate(value.At)}"
    };

    public static string FormatDate(DateTimeOffset? value) => value is null ? "—" : value.Value.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    public static IReadOnlyList<string> IanaTimeZones()
    {
        var values = new HashSet<string>(StringComparer.Ordinal) { "UTC", "Europe/Paris" };
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (zone.Id.Contains('/')) values.Add(zone.Id);
            else if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)) values.Add(iana);
        }
        return values.Order(StringComparer.Ordinal).ToArray();
    }

    public static DateTimeOffset ParseLocalInstant(DateTime value, string timeZone)
    {
        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        if (zone.IsInvalidTime(local)) throw new FormatException($"{value} does not exist in {timeZone} because of a daylight-saving transition.");
        if (zone.IsAmbiguousTime(local)) throw new FormatException($"{value} is ambiguous in {timeZone}; choose another time.");
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static DateTime LocalInput(DateTimeOffset value, string timeZone) => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime, DateTimeKind.Unspecified);
    public static string OccurrenceLabel(DateTimeOffset value, string timeZone, DateTimeOffset now) { var scheduled = TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(timeZone)); var relative = value <= now ? "due" : Relative(value - now); var sign = scheduled.Offset < TimeSpan.Zero ? "-" : "+"; return $"{scheduled:ddd d MMM yyyy · HH:mm} ({timeZone}, UTC{sign}{scheduled.Offset.Duration():hh\\:mm}) · {relative}"; }
    private static string Relative(TimeSpan value) => value.TotalDays >= 2 ? $"in {(int)value.TotalDays} days" : value.TotalHours >= 2 ? $"in {(int)value.TotalHours} hours" : $"in {Math.Max(1, (int)value.TotalMinutes)} minutes";
}
