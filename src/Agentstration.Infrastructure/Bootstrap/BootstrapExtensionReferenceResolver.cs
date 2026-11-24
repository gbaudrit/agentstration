using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Bootstrap;

public interface IBootstrapContributionResourceResolver
{
    string Kind { get; }
    Task<BootstrapResolvedReference> ResolveAsync(
        ExtensionRegistrationResource registration, BootstrapContributionSelector selector, CancellationToken cancellationToken);
}

public sealed class BootstrapExtensionReferenceResolver(
    IResourceStore store,
    IEnumerable<IBootstrapContributionResourceResolver> contributions) : IBootstrapExtensionReferenceResolver
{
    public async Task<BootstrapResolvedReference> ResolveExtensionAsync(
        BootstrapExtensionSelector selector, ResourceScopeRef consumerScope, CancellationToken cancellationToken)
    {
        var registration = await RegistrationAsync(selector, consumerScope, cancellationToken);
        return Resolved(registration.Value, registration.ETag);
    }

    public async Task<BootstrapResolvedReference> ResolveContributionAsync(
        BootstrapContributionSelector selector, ResourceScopeRef consumerScope, CancellationToken cancellationToken)
    {
        var registration = await RegistrationAsync(new() { Id = selector.ExtensionId, InstanceId = selector.InstanceId },
            consumerScope, cancellationToken);
        var resolver = contributions.SingleOrDefault(value => value.Kind == selector.Kind)
            ?? throw new BootstrapDependencyException("contribution_kind_unsupported", $"Bootstrap cannot resolve extension contribution kind '{selector.Kind}'.");
        var resolved = await resolver.ResolveAsync(registration.Value, selector, cancellationToken);
        return resolved with { Fingerprint = $"{registration.Value.Uid:D}:{registration.ETag}:{resolved.Fingerprint}" };
    }

    private async Task<StoredResource<ExtensionRegistrationResource>> RegistrationAsync(
        BootstrapExtensionSelector selector, ResourceScopeRef consumerScope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selector.Id) || selector.Id.Length > 256 || selector.InstanceId == Guid.Empty)
            throw new BootstrapDependencyException("extension_selector_invalid", "A public extension selector requires a bounded ID and a valid optional installation ID.");
        var visible = await store.ListVisibleAsync<ExtensionRegistrationResource>(consumerScope,
            ExtensionKinds.ExtensionRegistration, 0, 1001, cancellationToken);
        if (visible.Count > 1000)
            throw new BootstrapDependencyException("extension_resolution_limit", "Bootstrap extension resolution exceeds its bounded candidate window.");
        var candidates = visible
            .Where(value => value.Value.Definition.Enabled
                && value.Value.Definition.ExpectedExtensionId == selector.Id).ToArray();
        var requests = await store.ListExactAsync<AepEnrollmentRequestResource>(ResourceScopeRef.Instance,
            ExtensionKinds.AepEnrollmentRequest, 0, 1001, cancellationToken);
        if (requests.Count > 1000 || candidates.Length > 1000)
            throw new BootstrapDependencyException("extension_resolution_limit", "Bootstrap extension resolution exceeds its bounded candidate window.");
        if (selector.InstanceId is null && candidates.Length > 1)
            throw new BootstrapDependencyException("extension_ambiguous", $"Extension '{selector.Id}' has several visible installations; select instanceId explicitly.");
        // An installation selector binds to its durable server-owned enrollment,
        // never to a similarly named registration or a caller-supplied endpoint.
        candidates = candidates.Where(candidate =>
        {
            var enrolled = requests.Where(value => value.Value.Definition.RegistrationName == candidate.Value.Name
                && value.Value.Definition.TargetScopeRef == candidate.Value.ScopeRef
                && value.Value.Definition.ExtensionId == selector.Id).ToArray();
            if (enrolled.Length == 0) return selector.InstanceId is null;
            return enrolled.Any(value => value.Value.Definition.State == AepEnrollmentState.Available
                && (selector.InstanceId is null || selector.InstanceId == value.Value.Definition.InstanceId));
        }).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new BootstrapDependencyException("extension_unavailable", $"Required extension '{selector.Id}' installation '{selector.InstanceId}' is not available from '{consumerScope}'."),
            _ => throw new BootstrapDependencyException("extension_ambiguous", $"Extension '{selector.Id}' has several visible installations; select instanceId explicitly.")
        };
    }

    internal static BootstrapResolvedReference Resolved(Resource resource, string revision) => new(
        new(resource.Name, resource.ScopeRef, resource.Namespace), $"{resource.Uid:D}:{resource.Generation}:{revision}");

    internal static bool IsOwnedBy(Resource resource, ExtensionRegistrationResource registration) =>
        resource.Governance.Origin.Kind == ResourceOriginKind.Extension
        && resource.Governance.Origin.Reference == registration.Definition.ExpectedExtensionId
        && resource.Governance.ManagedBy?.Reference == InstallationReference(registration);

    internal static string InstallationReference(ExtensionRegistrationResource registration) =>
        $"{registration.ScopeRef!.Value.Value}/{registration.Kind}/{registration.Namespace}/{registration.Name}";

    internal static BootstrapDependencyException Missing(BootstrapContributionSelector selector) => new(
        "contribution_unavailable", $"Required {selector.Kind} contribution '{selector.ContributionId}' from extension '{selector.ExtensionId}' is not available with version '{selector.Version}'.");
}
