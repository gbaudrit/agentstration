using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
namespace Agentstration.Web.Console;

public sealed class KnowledgeSourceEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")] public string Name { get; set; } = string.Empty;
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    public string Namespace { get; set; } = "default";
    [StringLength(2000)] public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    public List<KnowledgeSourceBindingModel> Bindings { get; } = [];
    public bool Enabled { get; set; }
    public string OutputStorageKey { get; set; } = string.Empty;
    public string OutputPrefix { get; set; } = string.Empty;
    public Guid OutputCollectionUid { get; set; }
    public string ProjectionFlowKey { get; set; } = string.Empty;
    public string RetrievalFlowKey { get; set; } = string.Empty;
    public JsonElement AutomaticParameters { get; set; } = JsonSerializer.SerializeToElement(new { });
    public bool LifecycleEnabled { get; set; }
    public bool KeepHistory { get; set; }
    public bool AllowExpiredFallback { get; set; }
    public string ProjectionTtl { get; set; } = string.Empty;
    public string HistoryRetention { get; set; } = string.Empty;
    public FlowCallReference? Orchestration { get; set; }
    public IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> CompositionPoints { get; set; } = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>();
    public bool NamespaceValid { get { try { _ = ResourceNamespace.Parse(Namespace); return true; } catch (ArgumentException) { return false; } } }
    public bool LifecycleValid => !LifecycleEnabled || LifecycleDuration.TryParse(ProjectionTtl, out _)
        && (!KeepHistory || LifecycleDuration.TryParse(HistoryRetention, out var retention) && retention is not null);
    public KnowledgeProjectionPolicy? LifecycleSelection()
    {
        if (!LifecycleEnabled) return null;
        _ = LifecycleDuration.TryParse(ProjectionTtl, out var ttl); _ = LifecycleDuration.TryParse(HistoryRetention, out var retention);
        return new() { TimeToLive = ttl, KeepHistory = KeepHistory, HistoryRetention = KeepHistory ? retention : null, AllowExpiredOnRefreshFailure = AllowExpiredFallback };
    }
    public static TimeSpan? ParseMaximumAge(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        if (parts.Length != 3
            || !long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or > 59
            || hours > (long)TimeSpan.MaxValue.TotalHours)
            throw new ArgumentException("MaximumAgeFormat");
        try
        {
            return new TimeSpan(checked(hours * TimeSpan.TicksPerHour
                + minutes * TimeSpan.TicksPerMinute
                + seconds * TimeSpan.TicksPerSecond));
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("MaximumAgeFormat", exception);
        }
    }
    public static string FormatMaximumAge(TimeSpan? value) => value is null
        ? string.Empty
        : $"{(long)value.Value.TotalHours:00}:{value.Value.Minutes:00}:{value.Value.Seconds:00}";
}
public sealed class KnowledgeSourceBindingModel
{
    public KnowledgeDataSourceBinding? Original { get; set; }
    public string OriginalKey { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataSourceKey { get; set; } = string.Empty;
    public string AcquisitionTargetName { get; set; } = string.Empty;
    public IReadOnlyList<FlowCompositionCall> Transformations { get; set; } = [];
    public string TransformationFlowKey { get; set; } = string.Empty;
    public JsonElement Configuration { get; set; } = JsonSerializer.SerializeToElement(new { });
    public bool ConfigurationValid { get; set; } = true;
    public bool TargetsLoadFailed { get; set; }
    public bool Required { get; set; } = true;
    public bool RefreshIfNecessary { get; set; }
    public bool AllowStaleOnRefreshFailure { get; set; }
    public string MaximumFallbackAgeText { get; set; } = string.Empty;
    public string MaximumAgeText { get; set; } = string.Empty;
    public static KnowledgeSourceBindingModel From(KnowledgeDataSourceBinding value, ResourceNamespace owner, string transformationFlowKey) => new()
    {
        Original = value,
        OriginalKey = value.DataSource.ScopeRef is null ? string.Empty : $"{value.DataSource.ScopeRef.Value.Value}|{(value.DataSource.Namespace ?? owner).Value}|{value.DataSource.Name}",
        Name = value.Name,
        DataSourceKey = value.DataSource.ScopeRef is null ? string.Empty
            : $"{value.DataSource.ScopeRef.Value.Value}|{(value.DataSource.Namespace ?? owner).Value}|{value.DataSource.Name}",
        AcquisitionTargetName = value.AcquisitionTarget?.Name ?? string.Empty,
        TransformationFlowKey = transformationFlowKey,
        Transformations = value.Transformations,
        Description = value.Description,
        Configuration = value.Configuration.Clone(),
        Required = value.Required,
        RefreshIfNecessary = value.RefreshIfNecessary,
        AllowStaleOnRefreshFailure = value.AllowStaleOnRefreshFailure,
        MaximumFallbackAgeText = LifecycleDuration.Format(value.MaximumFallbackAge),
        MaximumAgeText = KnowledgeSourceEditorModel.FormatMaximumAge(value.MaximumAge)
    };
}
