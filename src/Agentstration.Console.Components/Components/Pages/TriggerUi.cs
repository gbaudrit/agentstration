using Agentstration.Management.Abstractions;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Components.Pages;

public static class TriggerUi
{
    public static string ScheduleLabel(TriggerSchedule? value, IStringLocalizer<TriggerStrings>? localizer = null) => value is null ? "—" : value.Type switch
    {
        TriggerScheduleType.Cron => $"{value.Expression} · {value.TimeZone}",
        TriggerScheduleType.Interval => Format(localizer, "Schedule.Interval", "Every {0} · from {1}", value.Every ?? "—", FormatDate(value.StartAt)),
        _ => Format(localizer, "Schedule.Once", "Once · {0}", FormatDate(value.At))
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
    public static string OccurrenceLabel(DateTimeOffset value, string timeZone, DateTimeOffset now, IStringLocalizer<TriggerStrings>? localizer = null) { var scheduled = TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(timeZone)); var relative = value <= now ? Text(localizer, "Due", "due") : Relative(value - now, localizer); var sign = scheduled.Offset < TimeSpan.Zero ? "-" : "+"; return $"{scheduled:ddd d MMM yyyy · HH:mm} ({timeZone}, UTC{sign}{scheduled.Offset.Duration():hh\\:mm}) · {relative}"; }
    private static string Relative(TimeSpan value, IStringLocalizer<TriggerStrings>? localizer) => value.TotalDays >= 2 ? Format(localizer, "Relative.Days", "in {0} days", (int)value.TotalDays) : value.TotalHours >= 2 ? Format(localizer, "Relative.Hours", "in {0} hours", (int)value.TotalHours) : Format(localizer, "Relative.Minutes", "in {0} minutes", Math.Max(1, (int)value.TotalMinutes));
    private static string Text(IStringLocalizer<TriggerStrings>? localizer, string key, string fallback) => localizer is null ? fallback : localizer[key].Value;
    private static string Format(IStringLocalizer<TriggerStrings>? localizer, string key, string fallback, params object[] arguments) => localizer is null ? string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, arguments) : localizer[key, arguments].Value;
}
