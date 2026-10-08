using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Knowledge;

public sealed class KnowledgeSnapshotException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record KnowledgeDurableArtifactEvidence(
    string ArtifactId,
    string ProducerFlowRunId,
    string ProducerFlowStepId,
    string StorageFlowRunId,
    string MediaType,
    long Length,
    string Sha256,
    IReadOnlyDictionary<string, string> Provenance);

public interface IKnowledgeSnapshotArtifactResolver
{
    Task<IReadOnlyList<KnowledgeDurableArtifactEvidence>> ResolveAsync(
        Guid workspaceId,
        IReadOnlyList<string> artifactIds,
        CancellationToken cancellationToken);
}

public sealed class KnowledgeSnapshotService(
    IResourceStore store,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    IKnowledgeSnapshotArtifactResolver artifacts,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public const int MaximumSnapshotArtifacts = 256;

    public async Task<KnowledgeSnapshotView> GetAsync(
        KnowledgeSnapshotId id,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var snapshot = await RequireSnapshotAsync(scopeRef, id.Value, id.Namespace, cancellationToken);
        return await ViewAsync(snapshot.Value, scopeRef, cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeSnapshotView>> ListAsync(
        KnowledgeSourceId sourceId,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await RequireSourceAsync(scopeRef, sourceId.Value, sourceId.Namespace, cancellationToken);
        var values = await store.ListExactAsync<KnowledgeSnapshotResource>(scopeRef,
            KnowledgeResourceKinds.KnowledgeSnapshot, 0, 1000, cancellationToken);
        var result = new List<KnowledgeSnapshotView>();
        foreach (var snapshot in values.Where(value => value.Value.KnowledgeSourceUid == source.Value.Uid)
                     .OrderByDescending(value => value.Value.PublishedAt))
            result.Add(await ViewAsync(snapshot.Value, scopeRef, cancellationToken));
        return result;
    }

    public async Task<KnowledgeSnapshotView?> GetActiveAsync(
        KnowledgeSourceId sourceId,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await RequireSourceAsync(scopeRef, sourceId.Value, sourceId.Namespace, cancellationToken);
        var observed = await LoadObservedAsync(scopeRef, source.Value, cancellationToken);
        if (observed?.Value.ActiveSnapshotName is null) return null;
        var snapshot = await store.GetExactAsync<KnowledgeSnapshotResource>(Scoped(scopeRef, sourceId.Namespace,
            KnowledgeResourceKinds.KnowledgeSnapshot, observed.Value.ActiveSnapshotName), cancellationToken);
        return snapshot is null
            ? throw Error("knowledge_snapshot_active_unavailable", "The active Knowledge Snapshot record is unavailable.")
            : await ViewAsync(snapshot.Value, scopeRef, cancellationToken);
    }

    public async Task<KnowledgeSnapshotResource> ResolveForRetrievalAsync(
        KnowledgeSourceId sourceId,
        string? snapshotName,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await RequireSourceAsync(scopeRef, sourceId.Value, sourceId.Namespace, cancellationToken);
        StoredResource<KnowledgeSnapshotResource> snapshot;
        if (string.IsNullOrWhiteSpace(snapshotName))
        {
            var observed = await LoadObservedAsync(scopeRef, source.Value, cancellationToken);
            if (observed?.Value.ActiveSnapshotName is null)
                throw Error("knowledge_snapshot_active_not_found",
                    $"KnowledgeSource '{source.Value.Address}' has no active Knowledge Snapshot.");
            snapshot = await RequireSnapshotAsync(scopeRef, observed.Value.ActiveSnapshotName,
                sourceId.Namespace, cancellationToken);
        }
        else
        {
            snapshot = await RequireSnapshotAsync(scopeRef, snapshotName.Trim(), sourceId.Namespace, cancellationToken);
        }
        if (snapshot.Value.KnowledgeSourceUid != source.Value.Uid)
            throw Error("knowledge_snapshot_source_mismatch",
                "The selected Knowledge Snapshot belongs to another KnowledgeSource.");
        _ = await artifacts.ResolveAsync(context.WorkspaceId,
            snapshot.Value.Artifacts.Select(value => value.ArtifactId).ToArray(), cancellationToken);
        return snapshot.Value;
    }

    public async Task<KnowledgeSnapshotView> SelectActiveAsync(
        KnowledgeSourceId sourceId,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.ResourcesWrite, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await RequireSourceAsync(scopeRef, sourceId.Value, sourceId.Namespace, cancellationToken);
        var snapshot = await RequireSnapshotAsync(scopeRef, snapshotName, sourceId.Namespace, cancellationToken);
        if (snapshot.Value.KnowledgeSourceUid != source.Value.Uid)
            throw Error("knowledge_snapshot_source_mismatch", "The Knowledge Snapshot belongs to another KnowledgeSource.");
        _ = await artifacts.ResolveAsync(context.WorkspaceId,
            snapshot.Value.Artifacts.Select(value => value.ArtifactId).ToArray(), cancellationToken);
        await SaveObservedAsync(scopeRef, source.Value, snapshot.Value, cancellationToken);
        await audit.WriteAsync(new(SecurityAuditActions.KnowledgeSnapshotActivated,
            ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
            WorkspaceId: context.WorkspaceId, ReasonCode: snapshotName), cancellationToken);
        return new(snapshot.Value, KnowledgeSnapshotLifecycleState.Active);
    }

    private async Task<KnowledgeSnapshotView> ViewAsync(
        KnowledgeSnapshotResource snapshot,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var observed = await LoadObservedAsync(scopeRef, new KnowledgeSourceResource
        {
            Metadata = new() { Name = snapshot.KnowledgeSourceName, Namespace = snapshot.KnowledgeSourceNamespace },
            Definition = new() { DisplayName = snapshot.KnowledgeSourceName },
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Uid = snapshot.KnowledgeSourceUid
        }, cancellationToken);
        try
        {
            _ = await artifacts.ResolveAsync(scopeRef.TargetId!.Value,
                snapshot.Artifacts.Select(value => value.ArtifactId).ToArray(), cancellationToken);
        }
        catch (KnowledgeSnapshotException)
        {
            return new(snapshot, KnowledgeSnapshotLifecycleState.Unavailable);
        }
        return new(snapshot, observed?.Value.ActiveSnapshotUid == snapshot.Uid
            ? KnowledgeSnapshotLifecycleState.Active
            : KnowledgeSnapshotLifecycleState.Superseded);
    }

    private async Task SaveObservedAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceResource source,
        KnowledgeSnapshotResource active,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await LoadObservedAsync(scopeRef, source, cancellationToken);
            var value = new KnowledgeSnapshotObservedResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSnapshotObservedState,
                Metadata = new() { Name = ObservedName(source.Uid), Namespace = source.Namespace },
                ScopeRef = scopeRef,
                Uid = current?.Value.Uid ?? Guid.Empty,
                Generation = current is null ? 1 : checked(current.Value.Generation + 1),
                Status = new() { ProvisioningState = ProvisioningState.Succeeded },
                KnowledgeSourceUid = source.Uid,
                ActiveSnapshotName = active.Name,
                ActiveSnapshotUid = active.Uid,
                LastProjectionId = active.ProjectionId,
                LastPublishedAt = active.PublishedAt,
                LastAttemptAt = timeProvider.GetUtcNow(),
                LastErrorCode = null,
                LastErrorMessage = null
            };
            try
            {
                _ = await store.PutExactAsync(scopeRef, value, current?.ETag, current is null, cancellationToken);
                return;
            }
            catch (ResourceConcurrencyException) when (attempt < 2) { }
        }
        throw Error("knowledge_snapshot_selection_conflict", "The active Knowledge Snapshot changed concurrently.");
    }

    private Task<StoredResource<KnowledgeSnapshotObservedResource>?> LoadObservedAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceResource source,
        CancellationToken cancellationToken) => store.GetExactAsync<KnowledgeSnapshotObservedResource>(Scoped(scopeRef,
            source.Namespace, KnowledgeResourceKinds.KnowledgeSnapshotObservedState, ObservedName(source.Uid)), cancellationToken);

    private async Task<StoredResource<KnowledgeSourceResource>> RequireSourceAsync(
        ResourceScopeRef scopeRef,
        string name,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken) => await store.GetExactAsync<KnowledgeSourceResource>(Scoped(scopeRef,
            @namespace, KnowledgeResourceKinds.KnowledgeSource, name), cancellationToken)
        ?? throw Error("knowledge_snapshot_source_not_found", $"KnowledgeSource '{@namespace}/{name}' was not found.");

    private async Task<StoredResource<KnowledgeSnapshotResource>> RequireSnapshotAsync(
        ResourceScopeRef scopeRef,
        string name,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken) => await store.GetExactAsync<KnowledgeSnapshotResource>(Scoped(scopeRef,
            @namespace, KnowledgeResourceKinds.KnowledgeSnapshot, name), cancellationToken)
        ?? throw Error("knowledge_snapshot_not_found", $"Knowledge Snapshot '{@namespace}/{name}' was not found.");

    private RequestContext RequireContext() => requestContext.IsInitialized
        ? requestContext.Current
        : throw Error("knowledge_snapshot_context_required", "A Workspace request context is required.");
    private static string ObservedName(Guid sourceUid) => $"snapshot-state-{sourceUid:N}";
    private static bool SameDigest(string left, string right) =>
        string.Equals(left.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase),
            right.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    private static string Hash(JsonElement value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
    private static string HashText(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ScopedResourceAddress Scoped(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string kind, string name) =>
        ScopedResourceAddress.Create(scopeRef, @namespace, kind, name);
    private static KnowledgeSnapshotException Error(string code, string message) => new(code, message);
}
