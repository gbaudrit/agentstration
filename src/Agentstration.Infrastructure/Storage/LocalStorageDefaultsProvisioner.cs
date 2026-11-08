using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

public sealed class LocalStorageDefaultsProvisioner(IResourceStore resources, StorageManagementService storages,
    StorageTypeManagementService types, IResourceAccessPolicyService policies, IResourceGovernanceContext governance) : IDisposable
{
    public const string DefaultName = "local-default";
    public const string WorkspaceAnnotation = "agentstration.io/local-storage-workspace";
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    public void Dispose() => gate.Dispose();

    public static ResourceScopeRef OwnerScope(Workspace workspace) => workspace.Kind switch
    {
        WorkspaceKind.InstanceOperations => ResourceScopeRef.Instance,
        WorkspaceKind.TenantOperations => ResourceScopeRef.Tenant(workspace.TenantId),
        _ => ResourceScopeRef.Workspace(workspace.Id)
    };

    public async Task EnsureAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var authority = governance.Push(ResourceGovernance.Core("storage.local-default", "1.0.0"));
            var scope = OwnerScope(workspace);
            var address = new ScopedResourceAddress(scope, ResourceNamespace.Default, StorageResourceKinds.Storage, DefaultName);
            if (await resources.GetExactAsync<StorageResource>(address, cancellationToken) is { } existing)
            {
                if (!existing.Value.Metadata.Annotations.TryGetValue(WorkspaceAnnotation, out var owner)
                    || owner != workspace.Id.ToString("N") || existing.Value.Governance.ManagedBy?.Reference != "storage.local-default")
                    throw new StorageException("storage_default_collision", "The reserved default Storage belongs to another owner.", 409);
                return; // Preserve identity, configuration, disablement, policy decisions and retained content.
            }
            var type = await types.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default,
                StoragePlatformResourceProvisioner.LocalTypeName, cancellationToken)
                ?? throw new StorageException("storage_type_unavailable", "The builtin local Storage type is unavailable.");
            if (scope != ResourceScopeRef.Instance)
                type = await InitializeTypeAccessAsync(type, scope, cancellationToken);
            var version = type.Value.Definition.Versions.Single(v => v.Version == StoragePlatformResourceProvisioner.ContentTypeVersion);
            var desired = new StorageResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = StorageResourceKinds.Storage,
                ScopeRef = scope,
                Metadata = new()
                {
                    Name = DefaultName,
                    Annotations = new Dictionary<string, string>
                    {
                        [ResourceProvenanceAnnotations.BuiltIn] = "true",
                        [WorkspaceAnnotation] = workspace.Id.ToString("N")
                    }
                },
                Definition = new()
                {
                    DisplayName = "Default local storage",
                    Type = new()
                    {
                        Type = new(type.Value.Name, type.Value.ScopeRef, type.Value.Namespace),
                        TypeUid = type.Value.Uid,
                        Version = version.Version,
                        SchemaDigest = version.SchemaDigest,
                        ContractDigest = version.ContractDigest
                    },
                    Configuration = JsonSerializer.SerializeToElement(new { }),
                    UsePolicy = scope.Kind == ResourceScopeKind.Workspace ? new()
                        : new() { Grants = [new(ResourceScopeRef.Workspace(workspace.Id))] },
                    Envelope = JsonSerializer.SerializeToElement(new StorageContentPolicy(Write: true), Json)
                }
            };
            try
            {
                var created = await storages.CreateAsync(desired, cancellationToken);
                if (scope.Kind != ResourceScopeKind.Workspace)
                    _ = await policies.PutAsync(new(new(created.Value.Uid, scope, created.Value.Namespace, created.Value.Kind),
                        [new(ResourceScopeRef.Workspace(workspace.Id), [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, cancellationToken);
            }
            catch (ResourceConcurrencyException)
            {
                var recovered = await resources.GetExactAsync<StorageResource>(address, cancellationToken);
                if (recovered?.Value.Metadata.Annotations.GetValueOrDefault(WorkspaceAnnotation) != workspace.Id.ToString("N")
                    || recovered.Value.Governance.ManagedBy?.Reference != "storage.local-default") throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task<StoredResource<StorageTypeResource>> InitializeTypeAccessAsync(StoredResource<StorageTypeResource> type,
        ResourceScopeRef target, CancellationToken cancellationToken)
    {
        using var authority = governance.Push(ResourceGovernance.Core("storage.local", StoragePlatformResourceProvisioner.ContentTypeVersion));
        var marker = "agentstration.io/local-access-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target.Value)))[..24];
        if (type.Value.Metadata.Annotations.ContainsKey(marker)) return type;
        // Record the one-time initialization decision before changing access. An interrupted initialization
        // never heals a missing grant automatically; administrators can inspect/repair it through #1064.
        var annotations = new Dictionary<string, string>(type.Value.Metadata.Annotations) { [marker] = "initialized" };
        type = await resources.PutExactAsync(type.Value.ScopeRef!.Value, type.Value with
        {
            Metadata = type.Value.Metadata with { Annotations = annotations },
            Generation = checked(type.Value.Generation + 1)
        }, type.ETag, false, cancellationToken);
        if (!type.Value.Definition.UsePolicy.Grants.Any(g => g.ScopeRef == target))
            type = await types.PutExactAsync(type.Value.ScopeRef!.Value, type.Value.Namespace, type.Value.Name,
                type.Value.Definition with
                {
                    UsePolicy = new() { Grants = [.. type.Value.Definition.UsePolicy.Grants, new(target)] }
                }, type.ETag, cancellationToken);
        var current = await policies.GetAsync(type.Value.Uid, cancellationToken);
        if (current?.Policy.Grants.Any(g => g.TargetScopeRef == target) == true) return type;
        _ = await policies.PutAsync(new(new(type.Value.Uid, type.Value.ScopeRef!.Value, type.Value.Namespace, type.Value.Kind),
            [.. current?.Policy.Grants ?? [], new(target, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), current?.ETag, cancellationToken);
        return type;
    }
}
