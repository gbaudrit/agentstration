using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Triggers;
using Agentstration.Web.Components.Pages;

namespace Agentstration.Web.Console;

/// <summary>The Trigger family's presentation draft; API commands and previews remain with its screen.</summary>
public sealed class TriggerEditorModel
{
    [Required, RegularExpression("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")] public string Name { get; set; } = "";
    [Required, StringLength(120)] public string DisplayName { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    public ResourceScopeRef? ScopeRef { get; set; }
    public string Tags { get; set; } = "";
    [Required, Display(Name = "Flow")] public string FlowKey { get; set; } = "";
    public bool TargetSelectionChanged { get; set; }
    public TriggerScheduleType ScheduleType { get; set; } = TriggerScheduleType.Once;
    [Required] public string TimeZone { get; set; } = "Europe/Paris";
    public DateTime LocalAt { get; set; }
    [Range(1, 100000)] public int IntervalValue { get; set; } = 6;
    [Required] public string IntervalUnit { get; set; } = "hours";
    [Required] public string CronExpression { get; set; } = "0 0 8 ? * MON-FRI";
    public TriggerMisfirePolicy MisfirePolicy { get; set; } = TriggerMisfirePolicy.FireOnce;
    public TriggerConcurrencyPolicy ConcurrencyPolicy { get; set; } = TriggerConcurrencyPolicy.Skip;
    [Required] public string InputJson { get; set; } = "{}";

    public TriggerSchedule BuildSchedule() => ScheduleType switch
    {
        TriggerScheduleType.Cron => new() { Type = TriggerScheduleType.Cron, Expression = CronExpression.Trim(), TimeZone = TimeZone },
        TriggerScheduleType.Interval => new() { Type = TriggerScheduleType.Interval, StartAt = TriggerUi.ParseLocalInstant(LocalAt, TimeZone), Every = IntervalUnit switch { "days" => $"P{IntervalValue}D", "hours" => $"PT{IntervalValue}H", _ => $"PT{IntervalValue}M" }, TimeZone = TimeZone },
        _ => new() { Type = TriggerScheduleType.Once, At = TriggerUi.ParseLocalInstant(LocalAt, TimeZone), TimeZone = TimeZone }
    };
    public JsonElement ParseInput(string objectRequiredMessage)
    {
        using var document = JsonDocument.Parse(InputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(objectRequiredMessage);
        return document.RootElement.Clone();
    }
    public static string TargetKey(TriggerFlowTarget target, ResourceNamespace owner) => $"{target.ScopeRef?.Value}|{(target.Namespace ?? owner).Value}|{target.Name}";
    public TriggerFlowTarget Target(TriggerResource? saved, string targetRequiredMessage)
    {
        if (!TargetSelectionChanged && saved?.Definition.Target.Flow is { } previous && FlowKey == TargetKey(previous, saved.Namespace)) return previous;
        var parts = FlowKey.Split('|', 3);
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[2])) throw new InvalidOperationException(targetRequiredMessage);
        return new() { Name = parts[2], Namespace = ResourceNamespace.Parse(parts[1]), ScopeRef = ResourceScopeRef.Parse(parts[0]) };
    }
    public TriggerProperties Definition(TriggerResource? saved, string targetRequiredMessage, string objectRequiredMessage) => new()
    {
        DisplayName = DisplayName.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Enabled = saved?.Definition.Enabled ?? false,
        Source = new() { Schedule = BuildSchedule() },
        Target = new() { Flow = Target(saved, targetRequiredMessage) },
        Input = ParseInput(objectRequiredMessage),
        MisfirePolicy = MisfirePolicy,
        ConcurrencyPolicy = ConcurrencyPolicy,
        ExecutionScope = saved?.Definition.ExecutionScope
    };
    public bool FieldsValid(params string[] fields) => fields.All(field => Validator.TryValidateProperty(GetType().GetProperty(field)!.GetValue(this), new ValidationContext(this) { MemberName = field }, null));
}
