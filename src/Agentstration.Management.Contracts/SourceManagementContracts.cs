using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Contracts;

public sealed record ImportSourceYamlRequest(string Manifest, ResourceScopeRef? ScopeRef = null);
public sealed record ImportSourceUrlRequest(string Url, ResourceScopeRef? ScopeRef = null);
public sealed record UpdateSourceDisplayNameRequest(string DisplayName);
public sealed record ConfigureSourceBindingsRequest(IReadOnlyList<SourceBindingSelection> Bindings);

public sealed record CreateSourceProviderRequest(
    string Name,
    SourceProviderProperties Properties,
    string Namespace = "default");
public sealed record PutSourceProviderRequest(SourceProviderProperties Properties);
public sealed record SourceProviderSummaryResponse(
    string Id,
    string Name,
    string DisplayName,
    string ExtensionName,
    string ExtensionNamespace,
    string ContributionId,
    string Status,
    string? Details,
    string Namespace = "default",
    ResourceScopeRef? ScopeRef = null);
public sealed record SourceProviderStatusResponse(
    string Provider,
    string Status,
    DateTimeOffset CheckedAt,
    string? Details);
public sealed record SourceProviderUsageResponse(
    ResourceScopeRef SourceScopeRef,
    string Publisher,
    string SourceName,
    string BindingName);
public sealed record SourceProviderUsagesResponse(
    IReadOnlyList<SourceProviderUsageResponse> Value,
    int Count);
