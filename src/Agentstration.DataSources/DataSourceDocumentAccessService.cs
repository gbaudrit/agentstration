using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.DataSources;

/// <summary>Queries exact retained acquisition evidence, including the first acquisition chosen by a dynamic request.</summary>
public sealed class DataSourceDocumentAccessService(DataSourceAcquisitionService acquisitions,
    DataSourceManagementService sources, IResourceStore store, IStorageDocumentGateway documents,
    ICurrentRequestContext current, IRequestContextScopeFactory scopes, IAuthorizationService authorization,
    DescendantResourceUseAuthorizer useAuthorizer, IOperationalTopologyProvisioner topology,
    IDataSourceDocumentCursorProtector protector, TimeProvider clock)
{
    private const int MaximumCursorLength = 32768;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DataSourceExecutionContext execution = new(current, scopes);

    private sealed record Stream(Guid WorkspaceId, Guid TenantId);
    private sealed record CursorState(DataSourceDocumentSelection Selection, string SelectorHash,
        Guid Principal, Guid Tenant, Guid Workspace, string Consumer, DateTimeOffset Expires,
        string LeaseId, StorageDocumentQuery Query);

    public async Task<DataSourceDocumentDescription> DescribeAsync(ResourceNamespace ns, string id, CancellationToken ct)
    {
        var acquisition = (await acquisitions.GetAsync(ns, id, ct)).Value;
        var publication = Publication(acquisition);
        var selection = Selection(acquisition, Contract(publication));
        var (source, _) = await AuthorizeAsync(selection, false, ct);
        var target = ReadTarget(publication.Target, source);
        var description = await documents.DescribeAsync(target, ct);
        return new(selection, acquisition.Name, acquisition.Uid, publication.Publication.Id, description);
    }

    public async Task<DataSourceDocumentQueryResult> QueryAcquisitionAsync(ResourceNamespace ns, string id,
        DataSourceDocumentQueryRequest request, CancellationToken ct) =>
        await QueryAsync((await DescribeAsync(ns, id, ct)).Selection, request, ct);

    public async Task<DataSourceDocumentDescription> ResolveAsync(DataSourceDocumentSelection selection, CancellationToken ct)
    {
        var (source, workspace) = await AuthorizeAsync(selection, false, ct);
        var acquisition = await ResolveAcquisitionAsync(selection, null, workspace, ct);
        var publication = Publication(acquisition);
        EnsureContract(selection, publication);
        var description = await documents.DescribeAsync(ReadTarget(publication.Target, source), ct);
        if (!description.Policy.Query || !description.QueryProfiles.Contains(selection.Contract.Profile))
            throw Error("data_source_document_contract_mismatch");
        return new(selection, acquisition.Name, acquisition.Uid, publication.Publication.Id, description);
    }

    public async Task<DataSourceDocumentQueryResult> QueryAsync(DataSourceDocumentSelection selection,
        DataSourceDocumentQueryRequest request, CancellationToken ct)
    {
        var (source, workspace) = await AuthorizeAsync(selection, true, ct);
        var caller = execution.RequireContext("data_source_context_required");
        CursorState session;
        DataSourceAcquisitionResource acquisition;
        if (request.Cursor is { } cursor)
        {
            if (cursor.Length is < 1 or > MaximumCursorLength || request.Query is not null || request.PageSize is not null
                || request.AcquisitionId is not null && (selection.Mode != DataSourceDocumentSelectionModes.Exact
                    || request.AcquisitionId != selection.AcquisitionId)) throw Error("data_source_document_cursor_parameters");
            try { session = JsonSerializer.Deserialize<CursorState>(protector.Unprotect(cursor), Json) ?? throw Error("data_source_document_cursor_invalid"); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { throw Error("data_source_document_cursor_invalid"); }
            if (session.Selection is null || session.Query is null || string.IsNullOrWhiteSpace(session.LeaseId)
                || session.Principal != caller.PrincipalId || session.Tenant != caller.TenantId || session.Workspace != caller.WorkspaceId
                || session.Consumer != execution.ConsumerScope.Value || session.SelectorHash != Hash(selection)
                || request.Profile is not null && request.Profile != selection.Contract.Profile)
                throw Error("data_source_document_cursor_invalid");
            if (session.Expires <= clock.GetUtcNow()) throw Error("data_source_document_cursor_expired");
            acquisition = await ResolveAcquisitionAsync(session.Selection, null, workspace, ct);
            if (!acquisition.ProjectionLeases.TryGetValue(session.LeaseId, out var expiry) || expiry < session.Expires)
                throw Error("data_source_document_cursor_expired");
        }
        else
        {
            if (request.Profile != selection.Contract.Profile || request.Query is not { ValueKind: JsonValueKind.Object } query
                || JsonSerializer.SerializeToUtf8Bytes(query).Length > 8192 || request.PageSize is < 1 or > 100)
                throw Error("data_source_document_query_invalid");
            acquisition = await ResolveAcquisitionAsync(selection, request.AcquisitionId, workspace, ct);
            var initialPublication = Publication(acquisition);
            EnsureContract(selection, initialPublication);
            var capabilities = await documents.DescribeAsync(ReadTarget(initialPublication.Target, source), ct);
            var size = request.PageSize ?? Math.Min(50, capabilities.Policy.MaximumPageSize);
            if (!capabilities.Policy.Query || !capabilities.QueryProfiles.Contains(selection.Contract.Profile)
                || size > capabilities.Policy.MaximumPageSize) throw Error("data_source_document_query_invalid");
            var exact = selection with { Mode = DataSourceDocumentSelectionModes.Exact, AcquisitionId = acquisition.Name, AcquisitionUid = acquisition.Uid };
            session = new(exact, Hash(selection), caller.PrincipalId, caller.TenantId, caller.WorkspaceId,
                execution.ConsumerScope.Value, clock.GetUtcNow().AddMinutes(4), "document-query-" + Guid.NewGuid().ToString("N"),
                new(Guid.NewGuid().ToString("N"), request.Profile!, query.Clone(), size));
            await ChangeLeaseAsync(acquisition, session.LeaseId, session.Expires, false, ct);
        }
        try
        {
            var publication = Publication(acquisition);
            EnsureContract(selection, publication);
            var target = ReadTarget(publication.Target, source);
            await documents.ValidatePublicationAsync(publication with { Target = target }, ct);
            var page = await documents.QueryAsync(target, session.Query with { RequestId = Guid.NewGuid().ToString("N") }, ct);
            var next = page.Cursor is null ? null : protector.Protect(JsonSerializer.Serialize(session with
            { Query = session.Query with { Cursor = page.Cursor } }, Json));
            if (next is { Length: > MaximumCursorLength }) throw Error("data_source_document_cursor_limit");
            if (next is null) await ChangeLeaseAsync(acquisition, session.LeaseId, session.Expires, true, ct);
            return new(acquisition.Name, acquisition.Uid, publication.Publication.Id, selection.Contract,
                page with { Cursor = null }, next, next is null ? null : session.Expires);
        }
        catch
        {
            try { await ChangeLeaseAsync(acquisition, session.LeaseId, session.Expires, true, ct); }
            catch (Exception exception) when (exception is ResourceConcurrencyException or DataSourceValidationException) { }
            throw;
        }
    }

    private async Task<(DataSourceResource Source, Stream Workspace)> AuthorizeAsync(DataSourceDocumentSelection selection, bool execute, CancellationToken ct)
    {
        var context = execution.RequireContext("data_source_context_required");
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, ct);
        if (execute) await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, ct);
        if (selection.Source?.ScopeRef is not { } owner || selection.Source.Namespace is null || selection.SourceUid == Guid.Empty
            || selection.Contract?.Profile is null || selection.Mode is not (DataSourceDocumentSelectionModes.Latest or DataSourceDocumentSelectionModes.Exact)
            || selection.Mode == DataSourceDocumentSelectionModes.Exact && (selection.AcquisitionId is not { Length: > 0 and <= 128 } || !selection.AcquisitionUid.HasValue || selection.AcquisitionUid.Value == Guid.Empty))
            throw Error("data_source_document_selection_invalid");
        if (selection.Mode == DataSourceDocumentSelectionModes.Latest && (selection.AcquisitionId is not null || selection.AcquisitionUid is not null))
            throw Error("data_source_document_selection_invalid");
        if (!await useAuthorizer.IsSameOrDescendantAsync(owner, selection.AcquisitionScope, ct)
            || !await useAuthorizer.IsSameOrDescendantAsync(selection.AcquisitionScope, execution.ConsumerScope, ct))
            throw Error("data_source_document_selection_forbidden");
        var source = (await sources.ResolveForUseAsync(selection.Source.Namespace.Value, selection.Source.Name, owner, execution.ConsumerScope, ct)).Value;
        if (source.Uid != selection.SourceUid || !source.Definition.Enabled) throw Error("data_source_document_source_unavailable");
        if (selection.AcquisitionScope.Kind == ResourceScopeKind.Workspace)
            return (source, new(context.WorkspaceId, context.TenantId));
        var operational = selection.AcquisitionScope.Kind == ResourceScopeKind.Instance
            ? await topology.GetInstanceOperationsWorkspaceAsync(ct)
            : await topology.GetTenantOperationsWorkspaceAsync(selection.AcquisitionScope.TargetId!.Value, ct);
        return (source, new(operational.Id, operational.TenantId));
    }

    private async Task<DataSourceAcquisitionResource> ResolveAcquisitionAsync(DataSourceDocumentSelection selection,
        string? requested, Stream workspace, CancellationToken ct)
    {
        if (requested is { Length: > 128 } || requested is not null && string.IsNullOrWhiteSpace(requested)
            || selection.Mode == DataSourceDocumentSelectionModes.Exact && requested is not null && requested != selection.AcquisitionId)
            throw Error("data_source_document_acquisition_mismatch");
        var id = selection.Mode == DataSourceDocumentSelectionModes.Exact ? selection.AcquisitionId : requested;
        DataSourceAcquisitionResource? acquisition;
        using (scopes.PushSystem())
        {
            if (id is not null)
                acquisition = (await store.GetExactAsync<DataSourceAcquisitionResource>(new(ResourceScopeRef.Workspace(workspace.WorkspaceId),
                    selection.Source.Namespace!.Value, DataSourceResourceKinds.DataSourceAcquisition, id), ct))?.Value;
            else
            {
                var records = await store.ListExactAsync<DataSourceAcquisitionResource>(ResourceScopeRef.Workspace(workspace.WorkspaceId),
                    DataSourceResourceKinds.DataSourceAcquisition, 0, 10001, ct);
                if (records.Count > 10000) throw Error("data_source_document_inventory_limit");
                acquisition = records.Select(r => r.Value).Where(a => Matches(a, selection, workspace)
                    && a.State == DataSourceAcquisitionState.Succeeded && a.ContentRetiredAt is null && a.ContentRetiringAt is null && a.Content is not null)
                    .OrderByDescending(a => a.CompletedAt ?? a.CreatedAt).ThenByDescending(a => a.Name, StringComparer.Ordinal).FirstOrDefault();
            }
        }
        if (acquisition is null || !Matches(acquisition, selection, workspace)
            || selection.Mode == DataSourceDocumentSelectionModes.Exact && acquisition.Uid != selection.AcquisitionUid)
            throw Error("data_source_document_acquisition_unavailable");
        _ = Publication(acquisition);
        return acquisition;
    }

    private static bool Matches(DataSourceAcquisitionResource a, DataSourceDocumentSelection s, Stream workspace) =>
        a.WorkspaceId == workspace.WorkspaceId && a.TenantId == workspace.TenantId && a.LogicalScopeRef == s.AcquisitionScope && a.DataSourceUid == s.SourceUid
        && a.DataSourceScopeRef == s.Source.ScopeRef && a.DataSourceName == s.Source.Name && a.DataSourceNamespace == s.Source.Namespace;

    private static StorageDocumentPublicationReference Publication(DataSourceAcquisitionResource acquisition)
    {
        if (acquisition.State != DataSourceAcquisitionState.Succeeded || acquisition.ContentRetiredAt is not null
            || acquisition.ContentRetiringAt is not null || acquisition.Content is not { } content
            || acquisition.Publication?.Document is not { } document || acquisition.Dataset?.Definition.DocumentCollectionUid is not { } collection)
            throw Error("data_source_document_acquisition_unsupported");
        var receipt = acquisition.Publication;
        var publication = document.Publication;
        if (receipt.Target.DocumentCollectionUid != collection || publication.Target.CollectionUid != collection
            || acquisition.Dataset.Definition.Storage != receipt.Target.Storage || publication.Target.Storage != receipt.Target.Storage
            || publication.Target.Publication != publication.Publication.Id || publication.Publication.Count != 1
            || document.DocumentId != content.Key || document.Revision != content.Version
            || receipt.Key != content.Key || receipt.Version != content.Version || receipt.Length != content.Length || receipt.Sha256 != content.Sha256
            || publication.QueryProfile.ModelDigest != publication.Publication.SchemaDigest)
            throw Error("data_source_document_publication_inconsistent");
        return publication;
    }

    private static DataSourceDocumentContractPin Contract(StorageDocumentPublicationReference publication) =>
        new(publication.QueryProfile, publication.SchemaSnapshot?.Source, publication.SchemaSnapshot?.SchemaUid,
            publication.SchemaSnapshot?.DefinitionHash, publication.SchemaSnapshot?.ParentDefinitionHash);

    private static void EnsureContract(DataSourceDocumentSelection selection, StorageDocumentPublicationReference publication)
    {
        if (Contract(publication) != selection.Contract) throw Error("data_source_document_contract_mismatch");
    }

    private static DataSourceDocumentSelection Selection(DataSourceAcquisitionResource acquisition, DataSourceDocumentContractPin contract) => new()
    {
        Source = new(acquisition.DataSourceName, acquisition.DataSourceScopeRef, acquisition.DataSourceNamespace), SourceUid = acquisition.DataSourceUid,
        AcquisitionScope = acquisition.LogicalScopeRef ?? throw Error("data_source_document_selection_invalid"), Contract = contract,
        Mode = DataSourceDocumentSelectionModes.Exact, AcquisitionId = acquisition.Name, AcquisitionUid = acquisition.Uid
    };

    private StorageDocumentTarget ReadTarget(StorageDocumentTarget target, DataSourceResource source) => target with
    {
        Consumer = new(execution.ConsumerScope, source.Address),
        Policy = target.Policy with { Put = false, Delete = false, Publish = false }
    };

    private async Task ChangeLeaseAsync(DataSourceAcquisitionResource acquisition, string id, DateTimeOffset expiry, bool release, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var system = scopes.PushSystem();
            var address = new ScopedResourceAddress(acquisition.ScopeRef!.Value, acquisition.Namespace, acquisition.Kind, acquisition.Name);
            var stored = await store.GetExactAsync<DataSourceAcquisitionResource>(address, ct);
            if (stored is null || stored.Value.Uid != acquisition.Uid) { if (release) return; throw Error("data_source_document_acquisition_unavailable"); }
            if (!release && (stored.Value.ContentRetiredAt is not null || stored.Value.ContentRetiringAt is not null))
                throw Error("data_source_document_acquisition_unavailable");
            var leases = stored.Value.ProjectionLeases.Where(p => p.Value > clock.GetUtcNow()).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (release) { if (!leases.Remove(id)) return; }
            else { if (leases.Count >= 32 && !leases.ContainsKey(id)) throw Error("data_source_document_cursor_capacity"); leases[id] = expiry; }
            try
            {
                _ = await store.PutExactAsync(acquisition.ScopeRef.Value, stored.Value with
                { Generation = checked(stored.Value.Generation + 1), ProjectionLeases = leases }, stored.ETag, false, ct);
                return;
            }
            catch (ResourceConcurrencyException) when (attempt < 2) { }
        }
    }

    private static string Hash(DataSourceDocumentSelection selection) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(selection, Json)));
    private static DataSourceValidationException Error(string code) => new(code, "The selected acquisition document access is unavailable or invalid.");
}
