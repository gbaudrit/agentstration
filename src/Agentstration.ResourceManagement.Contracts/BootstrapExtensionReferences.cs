using Agentstration.Resources;
using System.Text.Json;

namespace Agentstration.ResourceManagement.Contracts;

public sealed record BootstrapExtensionSelector
{
    public required string Id { get; init; }
    public Guid? InstanceId { get; init; }
}

public sealed record BootstrapContributionSelector
{
    public required string ExtensionId { get; init; }
    public Guid? InstanceId { get; init; }
    public required string Kind { get; init; }
    public required string ContributionId { get; init; }
    public string? Version { get; init; }
}

public sealed record BootstrapResolvedReference(ResourceReference Reference, string Fingerprint, JsonElement? TargetValue = null);

public interface IBootstrapExtensionReferenceResolver
{
    Task<BootstrapResolvedReference> ResolveExtensionAsync(
        BootstrapExtensionSelector selector, ResourceScopeRef consumerScope, CancellationToken cancellationToken);
    Task<BootstrapResolvedReference> ResolveContributionAsync(
        BootstrapContributionSelector selector, ResourceScopeRef consumerScope, CancellationToken cancellationToken);
}

public sealed class BootstrapDependencyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
