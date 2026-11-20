using System.ComponentModel.DataAnnotations;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;

namespace Agentstration.Web.Console;

public sealed class SourceEditorModel
{
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; set; }
    public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    public SourceRefreshPolicyForm SourceRefresh { get; set; } = new();
    public SourceRefreshPolicyForm ChannelRefresh { get; set; } = new();
    public Dictionary<string, SourceRefreshPolicyForm> ChannelOverrides { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SourceConsoleBindingSelection> BindingSelections { get; set; } = new(StringComparer.Ordinal);

    public static SourceEditorModel From(SourceConsoleDetailView value)
    {
        var configuration = value.Source.Configuration.Definition;
        var model = new SourceEditorModel
        {
            DisplayName = configuration.DisplayName,
            Name = value.Source.Source.Name,
            Namespace = value.Source.Source.Namespace.Value,
            ScopeRef = value.Source.Source.ScopeRef,
            Description = value.SelectedVersion.Definition.PublishedDefinition.Description,
            Tags = string.Join(Environment.NewLine, value.Source.Source.Metadata.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")),
            SourceRefresh = SourceRefreshPolicyForm.From(configuration.Refresh.Source),
            ChannelRefresh = SourceRefreshPolicyForm.From(configuration.Refresh.Channels)
        };
        // Removed channel overrides remain harmless saved configuration, as in ADR-0097.
        foreach (var (name, policy) in configuration.Refresh.ChannelOverrides)
            model.ChannelOverrides[name] = SourceRefreshPolicyForm.From(policy) with { Custom = true };
        foreach (var channel in value.SelectedVersion.Definition.PublishedDefinition.Channels)
            if (!model.ChannelOverrides.ContainsKey(channel.Name)) model.ChannelOverrides[channel.Name] = SourceRefreshPolicyForm.From(configuration.Refresh.Channels);
        foreach (var binding in value.Bindings.Bindings.Where(binding => binding.Target is not null))
            model.BindingSelections[binding.Name] = new(binding.Name, binding.TargetKind, binding.Target, null);
        return model;
    }
    public SourceRefreshConfiguration ToRefreshConfiguration() => new()
    {
        Source = SourceRefresh.ToPolicy(),
        Channels = ChannelRefresh.ToPolicy(),
        ChannelOverrides = ChannelOverrides.Where(pair => pair.Value.Custom).ToDictionary(pair => pair.Key, pair => pair.Value.ToPolicy(), StringComparer.Ordinal)
    };
    public IEnumerable<SourcePolicyValidation> ValidatePolicies()
    {
        foreach (var result in SourceRefresh.Validate(nameof(SourceRefresh))) yield return result;
        foreach (var result in ChannelRefresh.Validate(nameof(ChannelRefresh))) yield return result;
        foreach (var policy in ChannelOverrides.Values.Where(policy => policy.Custom))
            foreach (var result in policy.Validate(nameof(ChannelOverrides))) yield return result;
    }
}

public sealed record SourcePolicyValidation(SourceRefreshPolicyForm Model, string Field, string RootField, string MessageKey, int? Minimum = null, int? Maximum = null);

public sealed record SourceRefreshPolicyForm
{
    private SourceRefreshPolicy? original;
    public bool Custom { get; set; }
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 360;
    public int TimeoutSeconds { get; set; } = 30;
    public int MaximumAttempts { get; set; } = 3;
    public int InitialBackoffSeconds { get; set; } = 30;
    public int MaximumBackoffSeconds { get; set; } = 900;
    public int JitterSeconds { get; set; } = 15;
    public static SourceRefreshPolicyForm From(SourceRefreshPolicy policy) => new()
    {
        original = policy,
        Enabled = policy.Enabled,
        IntervalMinutes = Math.Max(1, policy.IntervalSeconds / 60),
        TimeoutSeconds = policy.TimeoutSeconds,
        MaximumAttempts = policy.MaximumAttempts,
        InitialBackoffSeconds = policy.InitialBackoffSeconds,
        MaximumBackoffSeconds = policy.MaximumBackoffSeconds,
        JitterSeconds = policy.JitterSeconds
    };
    public SourceRefreshPolicy ToPolicy() => new()
    {
        Enabled = Enabled,
        // Reading a seconds-based policy must not silently round it on an unrelated save.
        IntervalSeconds = original is not null && IntervalMinutes == Math.Max(1, original.IntervalSeconds / 60) ? original.IntervalSeconds : checked(IntervalMinutes * 60),
        TimeoutSeconds = TimeoutSeconds,
        MaximumAttempts = MaximumAttempts,
        InitialBackoffSeconds = InitialBackoffSeconds,
        MaximumBackoffSeconds = MaximumBackoffSeconds,
        JitterSeconds = JitterSeconds
    };
    public IEnumerable<SourcePolicyValidation> Validate(string root)
    {
        foreach (var (field, value, min, max) in new[]
        {
            (nameof(IntervalMinutes), IntervalMinutes, 1, 10080), (nameof(TimeoutSeconds), TimeoutSeconds, 1, 300),
            (nameof(MaximumAttempts), MaximumAttempts, 1, 10), (nameof(InitialBackoffSeconds), InitialBackoffSeconds, 1, 3600),
            (nameof(MaximumBackoffSeconds), MaximumBackoffSeconds, 1, 86400), (nameof(JitterSeconds), JitterSeconds, 0, 3600)
        })
            if (value < min || value > max) yield return new(this, field, root, "Validation.Range", min, max);
        if (MaximumBackoffSeconds < InitialBackoffSeconds)
            yield return new(this, nameof(MaximumBackoffSeconds), root, "RefreshBackoffOrder");
        var interval = original is not null && IntervalMinutes == Math.Max(1, original.IntervalSeconds / 60) ? original.IntervalSeconds : (long)IntervalMinutes * 60;
        if (JitterSeconds > Math.Min(3600, interval / 2))
            yield return new(this, nameof(JitterSeconds), root, "RefreshJitterBound");
    }
}

public sealed class SourceImportEditorModel
{
    public string Method { get; set; } = "url";
    public string Url { get; set; } = string.Empty;
    public string Yaml { get; set; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; set; }
    public bool HasManifest => Method == "url" ? Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" : !string.IsNullOrWhiteSpace(Yaml);
}
