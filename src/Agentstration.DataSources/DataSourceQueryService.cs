using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.DataSources;

/// <summary>Owns independently published queries; execution delegates to governed acquisition access.</summary>
public sealed class DataSourceQueryService(IResourceStore resources, IResourceScopeOperations scopeOperations,
    IResourceAccessPolicyService policies, DataSourceDocumentAccessService documents,
    ICurrentRequestContext current, IRequestContextScopeFactory scopes, IAuthorizationService authorization,
    IDataSourceDocumentCursorProtector protector, TimeProvider clock, ISecurityAuditWriter audit)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> publicationGates = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DataSourceExecutionContext execution = new(current, scopes);
    private sealed record Continuation(string Format, string ReferenceHash, DataSourceQueryReference Exact,
        Guid VersionUid, string DefinitionDigest, string InnerCursor);

    public async Task<IReadOnlyList<StoredResource<DataSourceQueryResource>>> ListAsync(ResourceScopeRef scope,
        Guid sourceUid, CancellationToken ct)
    {
        await ReadPermissionAsync(ct);
        var values = await resources.ListVisibleAsync<DataSourceQueryResource>(scope, DataSourceResourceKinds.DataSourceQuery, 0, 1001, ct);
        if (values.Count > 1000) throw Error("data_source_query_inventory_limit");
        return values.Where(v => v.Value.Definition.Selection.SourceUid == sourceUid).ToArray();
    }

    public async Task<StoredResource<DataSourceQueryResource>> GetAsync(ResourceScopeRef scope, ResourceNamespace ns, string name, CancellationToken ct)
    {
        await ReadPermissionAsync(ct);
        return await resources.GetExactAsync<DataSourceQueryResource>(new(scope, ns, DataSourceResourceKinds.DataSourceQuery, name), ct)
            ?? throw Error("data_source_query_not_found");
    }

    public async Task<StoredResource<DataSourceQueryResource>> CreateAsync(DataSourceQueryResource desired, CancellationToken ct)
    {
        await WritePermissionAsync(ct);
        var scope = desired.ScopeRef ?? execution.ConsumerScope;
        var definition = Normalize(desired.Definition);
        var value = desired with { ScopeRef = scope, Uid = Guid.NewGuid(), Generation = 1, Definition = definition };
        if (value.Metadata is null || value.Kind != DataSourceResourceKinds.DataSourceQuery || value.ApiVersion != ResourceApiVersions.CoreV1)
            throw Error("data_source_query_invalid");
        Name(value.Name);
        var saved = await scopeOperations.WriteAsync(value, scope, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            if (scope != definition.Selection.AcquisitionScope) throw Error("data_source_query_scope_invalid");
            _ = await documents.ResolveAsync(definition.Selection, token);
            return await resources.PutExactAsync(scope, value, null, true, token);
        }, ct);
        await AuditAsync(SecurityAuditActions.DataSourceQueryCreated, scope, ct);
        return saved;
    }

    public async Task<StoredResource<DataSourceQueryResource>> UpdateAsync(ResourceScopeRef scope, ResourceNamespace ns,
        string name, DataSourceQueryProperties definition, string? etag, CancellationToken ct)
    {
        await WritePermissionAsync(ct);
        var previous = await GetAsync(scope, ns, name, ct);
        definition = Normalize(definition);
        var old = previous.Value.Definition.Selection;
        if (definition.Selection.Source != old.Source || definition.Selection.SourceUid != old.SourceUid
            || definition.Selection.AcquisitionScope != old.AcquisitionScope) throw Error("data_source_query_source_immutable");
        var saved = await scopeOperations.WriteAsync(previous.Value, scope, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            if (definition.Enabled) _ = await documents.ResolveAsync(definition.Selection, token);
            return await resources.PutExactAsync(scope, previous.Value with
            { Definition = definition, Generation = checked(previous.Value.Generation + 1) }, etag, false, token);
        }, ct);
        await AuditAsync(SecurityAuditActions.DataSourceQueryUpdated, scope, ct);
        return saved;
    }

    public async Task<IReadOnlyList<DataSourceQueryVersionResource>> VersionsAsync(ResourceScopeRef scope,
        ResourceNamespace ns, string name, CancellationToken ct)
    {
        var owner = await GetAsync(scope, ns, name, ct);
        return await VersionsAsync(owner.Value, ct);
    }

    private async Task<IReadOnlyList<DataSourceQueryVersionResource>> VersionsAsync(DataSourceQueryResource owner, CancellationToken ct)
    {
        using var authorizedVersions = scopes.PushSystem();
        var values = await resources.ListExactAsync<DataSourceQueryVersionResource>(owner.ScopeRef!.Value,
            DataSourceResourceKinds.DataSourceQueryVersion, 0, 10001, ct);
        if (values.Count > 10000) throw Error("data_source_query_inventory_limit");
        return values.Select(v => v.Value).Where(v => v.QueryUid == owner.Uid && v.Namespace == owner.Namespace && v.QueryName == owner.Name)
            .OrderByDescending(v => v.PublishedAt).ThenByDescending(v => v.QueryGeneration).ThenByDescending(v => v.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<DataSourceQueryVersionResource> PublishAsync(ResourceScopeRef scope, ResourceNamespace ns,
        string name, string version, string? etag, CancellationToken ct)
    {
        await WritePermissionAsync(ct);
        var owner = await GetAsync(scope, ns, name, ct);
        var gate = publicationGates.GetOrAdd(owner.Value.Uid, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var result = await PublishCoreAsync(scope, ns, name, version, etag, ct);
            await AuditAsync(SecurityAuditActions.DataSourceQueryPublished, scope, ct);
            return result;
        }
        finally { gate.Release(); }
    }
    private async Task<DataSourceQueryVersionResource> PublishCoreAsync(ResourceScopeRef scope, ResourceNamespace ns,
        string name, string version, string? etag, CancellationToken ct)
    {
        Version(version);
        var draft = await GetAsync(scope, ns, name, ct);
        if (etag is null || etag != draft.ETag) throw new ResourceConcurrencyException("Reload the Query draft before publication.");
        return await scopeOperations.WriteAsync(draft.Value, scope, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            if (!draft.Value.Definition.Enabled) throw Error("data_source_query_disabled");
            _ = await documents.ResolveAsync(draft.Value.Definition.Selection, token);
            var existing = await resources.GetExactAsync<DataSourceQueryVersionResource>(new(scope, ns,
                DataSourceResourceKinds.DataSourceQueryVersion, VersionName(name, version)), token);
            if (existing is not null)
            {
                if (existing.Value.QueryUid == draft.Value.Uid && existing.Value.DefinitionDigest == Digest(draft.Value.Definition)) return existing.Value;
                throw Error("data_source_query_version_immutable");
            }
            // Take the same draft ETag immediately before publishing; publication snapshots never follow later draft edits.
            var latest = await resources.GetExactAsync<DataSourceQueryResource>(new(scope, ns, DataSourceResourceKinds.DataSourceQuery, name), token);
            if (latest?.ETag != etag) throw new ResourceConcurrencyException("The Query draft changed during publication.");
            var previous = await VersionsAsync(draft.Value, token);
            var now = clock.GetUtcNow();
            if (previous.Count > 0 && now <= previous[0].PublishedAt) now = previous[0].PublishedAt.AddTicks(1);
            var published = new DataSourceQueryVersionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceQueryVersion,
                Metadata = new() { Name = VersionName(name, version), Namespace = ns }, ScopeRef = scope, Generation = 1,
                QueryUid = draft.Value.Uid, QueryName = name, QueryGeneration = draft.Value.Generation,
                Version = version, Definition = draft.Value.Definition, ContractDigest = Contract(draft.Value.Definition),
                DefinitionDigest = Digest(draft.Value.Definition), PublishedAt = now
            };
            return (await resources.CreateImmutableAsync(published, token)).Value;
        }, ct);
    }

    public async Task<DataSourceQueryVersionResource> ResolveAsync(DataSourceQueryReference reference, CancellationToken ct)
    {
        if (reference.Query?.ScopeRef is not { } scope || reference.Query.Namespace is not { } ns || reference.QueryUid == Guid.Empty)
            throw Error("data_source_query_reference_invalid");
        var owner = (await GetAsync(scope, ns, reference.Query.Name, ct)).Value;
        if (owner.Uid != reference.QueryUid || !owner.Definition.Enabled) throw Error("data_source_query_unavailable");
        if (scope != execution.ConsumerScope)
        {
            var decision = await policies.EvaluateAsync(new(owner.Uid, scope, ns, owner.Kind, owner.Generation),
                execution.ConsumerScope, ResourceAccessAction.Use, ct);
            if (!decision.Allowed) throw Error("data_source_query_access_denied");
        }
        DataSourceQueryVersionResource? version;
        if (reference.Version is { } selected)
        {
            Version(selected);
            using var authorizedVersion = scopes.PushSystem();
            version = (await resources.GetExactAsync<DataSourceQueryVersionResource>(new(scope, ns,
                DataSourceResourceKinds.DataSourceQueryVersion, VersionName(owner.Name, selected)), ct))?.Value;
        }
        else version = (await VersionsAsync(owner, ct)).FirstOrDefault();
        if (version is null || version.QueryUid != owner.Uid || version.QueryName != owner.Name || version.Namespace != ns
            || version.DefinitionDigest != Digest(version.Definition) || version.ContractDigest != Contract(version.Definition))
            throw Error("data_source_query_version_unavailable");
        return version;
    }

    public async Task<DataSourceQueryExecutionResult> ExecuteAsync(DataSourceQueryReference reference,
        DataSourceQueryExecutionRequest request, string? expectedContract, DataSourceQueryAcquisitionPin? acquisition, CancellationToken ct)
    {
        await authorization.EnsurePermissionAsync(execution.RequireContext("data_source_context_required"), AuthorizationPermissions.RunsExecute, ct);
        DataSourceQueryVersionResource version;
        string? innerCursor = null;
        var referenceHash = Digest(new { reference, expectedContract, acquisition });
        if (request.Cursor is { } cursor)
        {
            if (cursor.Length is < 1 or > 65536 || request.PageSize is not null
                || request.AcquisitionId is not null && request.AcquisitionId != acquisition?.Id) throw Error("data_source_query_cursor_invalid");
            Continuation continuation;
            try { continuation = JsonSerializer.Deserialize<Continuation>(protector.Unprotect(cursor), Json) ?? throw new JsonException(); }
            catch (Exception e) when (e is not OperationCanceledException) { throw Error("data_source_query_cursor_invalid"); }
            if (continuation.Format != "query/v1" || continuation.ReferenceHash != referenceHash || continuation.Exact is null
                || string.IsNullOrEmpty(continuation.InnerCursor)) throw Error("data_source_query_cursor_invalid");
            version = await ResolveAsync(continuation.Exact, ct);
            if (version.Uid != continuation.VersionUid || version.DefinitionDigest != continuation.DefinitionDigest)
                throw Error("data_source_query_cursor_invalid");
            innerCursor = continuation.InnerCursor;
        }
        else version = await ResolveAsync(reference, ct);
        if (expectedContract is not null && version.ContractDigest != expectedContract) throw Error("data_source_query_contract_mismatch");
        var selection = version.Definition.Selection;
        if (acquisition is not null)
        {
            if (request.AcquisitionId is not null && request.AcquisitionId != acquisition.Id) throw Error("data_source_document_acquisition_mismatch");
            selection = selection with { Mode = DataSourceDocumentSelectionModes.Exact, AcquisitionId = acquisition.Id, AcquisitionUid = acquisition.Uid };
        }
        var result = await documents.QueryAsync(selection, new()
        {
            Profile = selection.Contract.Profile, Query = innerCursor is null ? version.Definition.Query : null,
            PageSize = innerCursor is null ? request.PageSize : null, AcquisitionId = request.AcquisitionId, Cursor = innerCursor
        }, ct);
        var exact = reference with { Version = version.Version };
        var next = result.NextCursor is null ? null : protector.Protect(JsonSerializer.Serialize(new Continuation("query/v1",
            referenceHash, exact, version.Uid, version.DefinitionDigest, result.NextCursor), Json));
        await AuditAsync(SecurityAuditActions.DataSourceQueryExecuted, execution.ConsumerScope, ct);
        return new(exact, version.Uid, version.DefinitionDigest, version.ContractDigest,
            result with { NextCursor = null }, next);
    }

    private Task WritePermissionAsync(CancellationToken ct) => authorization.EnsurePermissionAsync(execution.RequireContext("data_source_context_required"), AuthorizationPermissions.ResourcesWrite, ct);
    private Task AuditAsync(string action, ResourceScopeRef scope, CancellationToken ct) => audit.WriteAsync(new(action,
        TenantId: scope.Kind == ResourceScopeKind.Tenant ? scope.TargetId : null,
        WorkspaceId: scope.Kind == ResourceScopeKind.Workspace ? scope.TargetId : null), ct);
    private Task ReadPermissionAsync(CancellationToken ct) => authorization.EnsurePermissionAsync(
        execution.RequireContext("data_source_context_required"), AuthorizationPermissions.ResourcesRead, ct);
    private static DataSourceQueryProperties Normalize(DataSourceQueryProperties value)
    {
        if (value is null || value.Selection?.Contract?.Profile is null || string.IsNullOrWhiteSpace(value.DisplayName) || value.DisplayName.Length > 200
            || value.Query.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(value.Query).Length > 8192)
            throw Error("data_source_query_invalid");
        return value with { Query = value.Query.Clone(), Selection = value.Selection with
        { Mode = DataSourceDocumentSelectionModes.Latest, AcquisitionId = null, AcquisitionUid = null } };
    }
    internal static string Contract(DataSourceQueryProperties value)
    {
        // Filter/order/paging change values, not the exposed result shape. Unknown profile options remain conservative.
        var shape = value.Query.EnumerateObject().Where(p => value.Selection.Contract.Profile.Id != "odata/4.01"
            || p.Name is not ("$filter" or "$orderby" or "$top" or "$skip"))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value);
        return Digest(new { value.Selection.Source, value.Selection.SourceUid, value.Selection.AcquisitionScope, value.Selection.Contract, shape });
    }
    internal static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    private static string VersionName(string name, string version) => "query-" + Digest(new { name, version });
    private static void Version(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 32 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
            throw Error("data_source_query_version_invalid");
    }
    internal static void Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !char.IsAsciiLetterOrDigit(name[0])
            || name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not '.' and not '-'))
            throw Error("data_source_query_name_invalid");
    }
    internal static DataSourceValidationException Error(string code) => new(code, "The Data Source Query is unavailable or invalid.");
}
