using System.ComponentModel.DataAnnotations;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class SourceRegistryEditorModel
{
    [Required, RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    public string Name { get; set; } = string.Empty;
    [Required, StringLength(256)] public string DisplayName { get; set; } = string.Empty;
    [Required, Url] public string IndexUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public SourceRegistryTrustPolicy TrustPolicy { get; set; } = SourceRegistryTrustPolicy.Untrusted;
    public SourceRegistryAuthenticationMode AuthenticationMode { get; set; }
    public string CredentialName { get; set; } = string.Empty;
    public bool AllowHttp { get; set; }
    public bool AllowPrivateNetwork { get; set; }
    public bool PeriodicEnabled { get; set; }
    [Range(1, 43200)] public int IntervalMinutes { get; set; } = 1440;
    [Range(1, 300)] public int TimeoutSeconds { get; set; } = 30;
    [Range(1, 10)] public int MaximumAttempts { get; set; } = 3;
    [Range(1, 86400)] public int InitialBackoffSeconds { get; set; } = 30;
    [Range(1, 86400)] public int MaximumBackoffSeconds { get; set; } = 900;
    [Range(0, 3600)] public int JitterSeconds { get; set; } = 15;
    [Range(1, 129600)] public int StaleAfterMinutes { get; set; } = 2880;
    [Range(1, 100)] public int RetainedObservations { get; set; } = 3;

    public SourceRegistryRegistrationProperties ToProperties() => new()
    {
        DisplayName = DisplayName.Trim(),
        IndexUrl = new Uri(IndexUrl.Trim(), UriKind.Absolute),
        Enabled = Enabled,
        TrustPolicy = TrustPolicy,
        AuthenticationMode = AuthenticationMode,
        Credential = AuthenticationMode == SourceRegistryAuthenticationMode.StaticBearer && !string.IsNullOrWhiteSpace(CredentialName)
            ? new ResourceReference(CredentialName, ResourceScopeRef.Instance, ResourceNamespace.Default)
            : null,
        EndpointPolicy = new() { AllowHttp = AllowHttp, AllowPrivateNetwork = AllowPrivateNetwork },
        RefreshPolicy = new()
        {
            PeriodicEnabled = PeriodicEnabled,
            Interval = TimeSpan.FromMinutes(IntervalMinutes),
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            MaximumAttempts = MaximumAttempts,
            InitialBackoff = TimeSpan.FromSeconds(InitialBackoffSeconds),
            MaximumBackoff = TimeSpan.FromSeconds(MaximumBackoffSeconds),
            Jitter = TimeSpan.FromSeconds(JitterSeconds),
            StaleAfter = TimeSpan.FromMinutes(StaleAfterMinutes)
        },
        CachePolicy = new() { RetainedObservations = RetainedObservations }
    };

    public static SourceRegistryEditorModel From(SourceRegistryRegistrationResource resource) => new()
    {
        Name = resource.Name,
        DisplayName = resource.Definition.DisplayName,
        IndexUrl = resource.Definition.IndexUrl.AbsoluteUri,
        Enabled = resource.Definition.Enabled,
        TrustPolicy = resource.Definition.TrustPolicy,
        AuthenticationMode = resource.Definition.AuthenticationMode,
        CredentialName = resource.Definition.Credential?.Name ?? string.Empty,
        AllowHttp = resource.Definition.EndpointPolicy.AllowHttp,
        AllowPrivateNetwork = resource.Definition.EndpointPolicy.AllowPrivateNetwork,
        PeriodicEnabled = resource.Definition.RefreshPolicy.PeriodicEnabled,
        IntervalMinutes = checked((int)resource.Definition.RefreshPolicy.Interval.TotalMinutes),
        TimeoutSeconds = checked((int)resource.Definition.RefreshPolicy.Timeout.TotalSeconds),
        MaximumAttempts = resource.Definition.RefreshPolicy.MaximumAttempts,
        InitialBackoffSeconds = checked((int)resource.Definition.RefreshPolicy.InitialBackoff.TotalSeconds),
        MaximumBackoffSeconds = checked((int)resource.Definition.RefreshPolicy.MaximumBackoff.TotalSeconds),
        JitterSeconds = checked((int)resource.Definition.RefreshPolicy.Jitter.TotalSeconds),
        StaleAfterMinutes = checked((int)resource.Definition.RefreshPolicy.StaleAfter.TotalMinutes),
        RetainedObservations = resource.Definition.CachePolicy.RetainedObservations
    };
}
