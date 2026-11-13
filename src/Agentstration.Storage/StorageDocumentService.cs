using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Storage;

public sealed class StorageDocumentService(StorageManagementService storages, IStorageDocumentGateway documents,
    ICurrentRequestContext current)
{
    public ResourceScopeRef ConsumerScope => current.AccessMode switch
    {
        ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
        ControlPlaneAccessMode.TenantAdministration or ControlPlaneAccessMode.Tenant => ResourceScopeRef.Tenant(current.Current.TenantId),
        _ => ResourceScopeRef.Workspace(current.Current.WorkspaceId)
    };
    public async Task<StorageDocumentTarget> ResolveTargetAsync(ResourceScopeRef owner, ResourceNamespace ns,
        string storageName, Guid collectionUid, CancellationToken ct)
    {
        var stored = await storages.GetExactAsync(owner, ns, storageName, ct)
            ?? throw new StorageException("storage_not_found", "The selected Storage is unavailable.", 404);
        var collection = stored.Value.Definition.Collections.SingleOrDefault(c => c.Uid == collectionUid && c.Enabled)
            ?? throw new StorageException("storage_collection_unavailable", "The exact collection is unavailable.", 403);
        return new(new(new(owner, ns, StorageResourceKinds.Storage, storageName), stored.Value.Uid,
            stored.Value.Generation, StorageDigests.Binding(stored.Value.Definition)), new(ConsumerScope, stored.Value.Address), collectionUid, collection.Policy);
    }
    public Task<StorageDocumentDescription> DescribeAsync(StorageDocumentTarget target, CancellationToken ct) => documents.DescribeAsync(target, ct);
    public Task<StorageDocumentValue> GetAsync(StorageDocumentTarget target, string id, string? revision, CancellationToken ct) => documents.GetAsync(target, id, revision, ct);
    public Task<StorageDocumentValue> PutAsync(StorageDocumentTarget target, StorageDocumentWrite request, CancellationToken ct) => documents.PutAsync(target, request, ct);
    public Task<StorageDocumentValue> DeleteAsync(StorageDocumentTarget target, StorageDocumentDelete request, CancellationToken ct) => documents.DeleteAsync(target, request, ct);
    public Task<StorageDocumentPage> QueryAsync(StorageDocumentTarget target, StorageDocumentQuery request, CancellationToken ct) => documents.QueryAsync(target, request, ct);
}
