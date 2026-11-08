using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.Storage;

public sealed record LocalStorageDefaultProvisioning(StorageAvailability Availability, StorageDiagnostic? Diagnostic = null);

public sealed partial class LocalStorageDefaultsProvisioner(IResourceStore resources, StorageManagementService storages,
    StorageTypeManagementService types, IResourceGovernanceContext governance,
    ILogger<LocalStorageDefaultsProvisioner> logger) : IDisposable
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

    internal static bool IsOwnedDefault(StorageResource resource, Workspace workspace) =>
        resource.Metadata.Annotations.GetValueOrDefault(WorkspaceAnnotation) == workspace.Id.ToString("N")
        && resource.Governance.ManagedBy is { Kind: ResourceManagementAuthorityKind.Core, Reference: "storage.local-default" };

    public async Task<LocalStorageDefaultProvisioning> EnsureAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var authority = governance.Push(ResourceGovernance.Core("storage.local-default", "1.0.0"));
            var scope = OwnerScope(workspace);
            var address = new ScopedResourceAddress(scope, ResourceNamespace.Default, StorageResourceKinds.Storage, DefaultName);
            if (await resources.GetExactAsync<StorageResource>(address, cancellationToken) is { } existing)
            {
                EnsureOwner(existing.Value, workspace);
                return await ObserveAsync(existing.Value, workspace, cancellationToken);
            }
            try
            {
                var type = await types.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default,
                    StoragePlatformResourceProvisioner.LocalTypeName, cancellationToken)
                    ?? throw new StorageException("storage_type_unavailable", "The builtin local Storage type is unavailable.");
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
                        Envelope = JsonSerializer.SerializeToElement(new StorageContentPolicy(Write: true), Json)
                    }
                };
                // Reuse the owning service's current generic Use and resource validation. Provisioning changes no grants.
                var created = await storages.CreateAsync(desired, cancellationToken);
                return await ObserveAsync(created.Value, workspace, cancellationToken);
            }
            catch (ResourceConcurrencyException)
            {
                var recovered = await resources.GetExactAsync<StorageResource>(address, cancellationToken);
                if (recovered is null) throw;
                EnsureOwner(recovered.Value, workspace);
                return await ObserveAsync(recovered.Value, workspace, cancellationToken);
            }
            catch (StorageException exception)
            {
                return Unavailable(workspace, exception.Code);
            }
        }
        finally { gate.Release(); }
    }

    private async Task<LocalStorageDefaultProvisioning> ObserveAsync(StorageResource resource, Workspace workspace,
        CancellationToken cancellationToken)
    {
        if (!resource.Definition.Enabled) return new(StorageAvailability.Disabled);
        try
        {
            _ = await storages.ResolveAsync(new(new(resource.ScopeRef!.Value, resource.Namespace, resource.Kind, resource.Name),
                resource.Uid, resource.Generation, StorageDigests.Binding(resource.Definition)),
                new(OwnerScope(workspace), resource.Address), cancellationToken);
            return new(StorageAvailability.Ready);
        }
        catch (StorageException exception) { return Unavailable(workspace, exception.Code); }
    }

    private LocalStorageDefaultProvisioning Unavailable(Workspace workspace, string code)
    {
        DefaultUnavailable(workspace.Id, OwnerScope(workspace).Value, code);
        return new(StorageAvailability.Unavailable, new(code, "The default local Storage is unavailable under the existing resource access and type restrictions."));
    }

    private static void EnsureOwner(StorageResource resource, Workspace workspace)
    {
        if (!IsOwnedDefault(resource, workspace))
            throw new StorageException("storage_default_collision", "The reserved default Storage belongs to another owner.", 409);
    }

    [LoggerMessage(LogLevel.Warning, "Default local Storage for Workspace {WorkspaceId} at {ScopeRef} is unavailable: {Code}.")]
    private partial void DefaultUnavailable(Guid workspaceId, string scopeRef, string code);
}
