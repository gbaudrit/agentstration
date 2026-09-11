using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed record AepEnrollmentPolicyOptions
{
    public const string SectionName = "Agentstration:Aep:Enrollment";
    public bool PairingCodeAllowed { get; init; } = true;
    public bool SharedKeyFileAllowed { get; init; } = true;
}

public sealed class AepEnrollmentSettingsService(
    IResourceStore store,
    IAuthorizationService authorization,
    ICurrentRequestContext requestContext,
    AepEnrollmentPolicyOptions? policy = null)
{
    private const string SettingsName = "default";
    private readonly AepEnrollmentPolicyOptions policy = policy ?? new();

    public async Task<AepEnrollmentSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        using var scope = RequestScopes().PushSystem();
        var stored = await GetStoredAsync(cancellationToken);
        return Snapshot(stored?.Value.Definition ?? new(), stored?.ETag);
    }

    public async Task<AepEnrollmentSettingsSnapshot> UpdateAsync(
        RequestContext context,
        bool pairingCodeEnabled,
        bool sharedKeyFileEnabled,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.ResourcesWrite, cancellationToken);
        using var scope = RequestScopes().PushSystem();
        var existing = await GetStoredAsync(cancellationToken);
        var definition = new AepEnrollmentSettingsProperties
        {
            PairingCodeEnabled = policy.PairingCodeAllowed && pairingCodeEnabled,
            SharedKeyFileEnabled = policy.SharedKeyFileAllowed && sharedKeyFileEnabled
        };
        var resource = existing?.Value ?? new AepEnrollmentSettingsResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AepEnrollmentSettings,
            Metadata = new ResourceMetadata { Name = SettingsName },
            ScopeRef = ResourceScopeRef.Instance
        };
        var stored = await store.PutExactAsync(
            ResourceScopeRef.Instance,
            resource with
            {
                Generation = existing is null ? 1 : checked(resource.Generation + 1),
                Definition = definition,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            },
            existing is null ? null : ifMatch,
            existing is null,
            cancellationToken);
        return Snapshot(stored.Value.Definition, stored.ETag);
    }

    public async Task<bool> IsEnabledAsync(AepEnrollmentMode mode, CancellationToken cancellationToken)
    {
        var settings = await GetAsync(cancellationToken);
        return mode switch
        {
            AepEnrollmentMode.Disabled => true,
            AepEnrollmentMode.PairingCode => settings.PairingCodeEnabled,
            AepEnrollmentMode.SharedKeyFile => settings.SharedKeyFileEnabled,
            _ => false
        };
    }

    private Task<StoredResource<AepEnrollmentSettingsResource>?> GetStoredAsync(CancellationToken cancellationToken) =>
        store.GetExactAsync<AepEnrollmentSettingsResource>(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, ResourceKinds.AepEnrollmentSettings, SettingsName),
            cancellationToken);

    private IRequestContextScopeFactory RequestScopes() => requestContext as IRequestContextScopeFactory
        ?? throw new InvalidOperationException("AEP enrollment settings require a mutable Control Plane request context.");

    private AepEnrollmentSettingsSnapshot Snapshot(AepEnrollmentSettingsProperties settings, string? etag) => new(
        policy.PairingCodeAllowed && settings.PairingCodeEnabled,
        policy.SharedKeyFileAllowed && settings.SharedKeyFileEnabled,
        policy.PairingCodeAllowed,
        policy.SharedKeyFileAllowed,
        etag);
}
