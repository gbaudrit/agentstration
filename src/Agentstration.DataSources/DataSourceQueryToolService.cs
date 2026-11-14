using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.DataSources;

public interface IDataSourceQueryToolResolver
{
    Task<ToolRelayTarget> ResolveAsync(ResourceScopeRef consumer, CancellationToken ct);
}

public sealed class DataSourceQueryToolService(DataSourceQueryService queries, ToolDefinitionService definitions,
    IDataSourceQueryToolResolver native, DataSourceDocumentAccessService documents, IResourceStore resources, ICurrentRequestContext current,
    IRequestContextScopeFactory scopes, IAuthorizationService authorization)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DataSourceExecutionContext execution = new(current, scopes);
    public const string GenericName = "datasource-query";
    public const string GenericVersion = "1.0.0";
    public async Task<ResourceReference> EnsureGenericAsync(ResourceScopeRef scope, ResourceNamespace ns, CancellationToken ct)
    {
        await authorization.EnsurePermissionAsync(execution.RequireContext("data_source_context_required"), AuthorizationPermissions.ResourcesWrite, ct);
        var existing = await definitions.GetExactAsync(GenericName, ns, scope, ct);
        var target = await native.ResolveAsync(scope, ct);
        if (existing is not null)
        {
            if (!existing.Value.Metadata.Annotations.ContainsKey("agentstration.io/query-generic")
                || existing.Value.Definition is not RelayToolDefinitionProperties relay || relay.Target != target || relay.Bindings.Count != 0)
                throw DataSourceQueryService.Error("data_source_query_tool_conflict");
        }
        else
        {
            _ = await definitions.PutAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolDefinition, ScopeRef = scope,
                Metadata = new() { Name = GenericName, Namespace = ns, Annotations = new Dictionary<string, string>
                    { ["agentstration.io/query-generic"] = "1" } },
                Definition = new RelayToolDefinitionProperties { DisplayName = "Data Source Query", Version = GenericVersion,
                    Description = "Execute a named, published Data Source Query. Omit the Query version for the latest publication. Query and acquisition stay fixed during pagination.",
                    Publish = true, Target = target, Bindings = [] }
            }, null, true, ct);
        }
        return new(AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName(GenericName, GenericVersion)), scope, ns);
    }
    public async Task<ResourceReference> PublishAsync(DataSourceQueryReference reference, PublishDataSourceQueryToolRequest request, CancellationToken ct)
    {
        await authorization.EnsurePermissionAsync(execution.RequireContext("data_source_context_required"), AuthorizationPermissions.ResourcesWrite, ct);
        DataSourceQueryService.Name(request.Name);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200) throw DataSourceQueryService.Error("data_source_query_name_invalid");
        reference = reference with { Version = request.QueryVersion };
        var selected = await queries.ResolveAsync(reference, ct);
        var scope = execution.ConsumerScope;
        var ns = reference.Query.Namespace!.Value;
        var existing = await definitions.GetExactAsync(request.Name, ns, scope, ct);
        if (existing is not null && (!existing.Value.Metadata.Annotations.TryGetValue("agentstration.io/query-uid", out var owner)
            || owner != reference.QueryUid.ToString("D"))) throw DataSourceQueryService.Error("data_source_query_tool_conflict");
        // Verify the selected acquisition and query contract without physically executing a query.
        var selection = selected.Definition.Selection;
        if (request.Acquisition is not null)
            selection = selection with { Mode = DataSourceDocumentSelectionModes.Exact,
                AcquisitionId = request.Acquisition.Id, AcquisitionUid = request.Acquisition.Uid };
        _ = await documents.ResolveAsync(selection, ct);
        List<ToolArgumentBinding> bindings =
        [
            new LiteralToolArgumentBinding { Path = "/query", Value = JsonSerializer.SerializeToElement(reference, Json) },
            new LiteralToolArgumentBinding { Path = "/contract", Value = JsonSerializer.SerializeToElement(selected.ContractDigest) }
        ];
        if (request.Acquisition is not null)
        {
            bindings.Add(new LiteralToolArgumentBinding { Path = "/acquisition", Value = JsonSerializer.SerializeToElement(request.Acquisition, Json) });
            bindings.Add(new LiteralToolArgumentBinding { Path = "/request/acquisitionId", Value = JsonSerializer.SerializeToElement(request.Acquisition.Id) });
        }
        var generic = await EnsureGenericAsync(scope, ns, ct);
        var genericTool = (await resources.GetExactAsync<ToolResource>(new(scope, ns, ToolResourceKinds.Tool, generic.Name), ct))!.Value;
        try
        {
            _ = await definitions.PutAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolDefinition, ScopeRef = scope,
                Metadata = new() { Name = request.Name, Namespace = ns, Annotations = new Dictionary<string, string>
                { ["agentstration.io/query-uid"] = reference.QueryUid.ToString("D"),
                  ["agentstration.io/data-source-uid"] = selected.Definition.Selection.SourceUid.ToString("D") } },
                Definition = new RelayToolDefinitionProperties
                {
                    DisplayName = request.DisplayName, Version = request.Version, Publish = true,
                    Description = "Execute published Data Source Query " + reference.Query.Name + (reference.Version is null
                        ? "; resolve the latest published Query version at each initial call." : "; use exact Query version " + reference.Version + ".")
                        + " Continue with cursor only. Query and acquisition stay fixed throughout pagination.",
                    Target = new() { Name = genericTool.Name, Namespace = ns, ScopeRef = scope, Uid = genericTool.Uid, Generation = genericTool.Generation }, Bindings = bindings, RequiresApproval = request.RequiresApproval
                }
            }, existing?.ETag, existing is null, ct);
        }
        catch (ToolDefinitionValidationException e) { throw new DataSourceValidationException(e.Code, "The Query Tool publication is invalid."); }
        return new(AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName(request.Name, request.Version)), scope, ns);
    }

    public async Task<IReadOnlyList<DataSourceQueryToolPublication>> ListAsync(ResourceScopeRef scope, Guid sourceUid, CancellationToken ct)
    {
        var queryValues = await queries.ListAsync(scope, sourceUid, ct);
        var ownedQueries = queryValues.ToDictionary(q => q.Value.Uid, q => q.Value);
        var versions = await resources.ListVisibleAsync<ToolDefinitionVersionResource>(scope, ToolResourceKinds.ToolDefinitionVersion, 0, 10001, ct);
        if (versions.Count > 10000) throw DataSourceQueryService.Error("data_source_query_inventory_limit");
        List<DataSourceQueryToolPublication> result = [];
        foreach (var version in versions.Select(v => v.Value))
        {
            if (version.Definition is not RelayToolDefinitionProperties relay) continue;
            if (version.DefinitionName == GenericName && version.Metadata.Annotations.ContainsKey("agentstration.io/query-generic"))
            {
                result.Add(new(new(AgentstrationToolProvider.ToolResourceName(version.Name), version.ScopeRef, version.Namespace),
                    Guid.Empty, "", relay.Version, null, null, Generic: true, DisplayName: relay.DisplayName));
                continue;
            }
            var reference = relay.Bindings.OfType<LiteralToolArgumentBinding>().FirstOrDefault(b => b.Path == "/query");
            if (reference is not null)
            {
                DataSourceQueryReference? query;
                try { query = reference.Value.Deserialize<DataSourceQueryReference>(Json); } catch (JsonException) { continue; }
                if (query is null || !ownedQueries.TryGetValue(query.QueryUid, out var owner)
                    || query.Query.Name != owner.Name || query.Query.ScopeRef != owner.ScopeRef || query.Query.Namespace != owner.Namespace) continue;
                var acquisition = relay.Bindings.OfType<LiteralToolArgumentBinding>().FirstOrDefault(b => b.Path == "/acquisition")?.Value
                    .Deserialize<DataSourceQueryAcquisitionPin>(Json);
                result.Add(new(new(AgentstrationToolProvider.ToolResourceName(version.Name), version.ScopeRef, version.Namespace),
                    query.QueryUid, query.Query.Name, relay.Version, query.Version, acquisition, DisplayName: relay.DisplayName));
            }
            else
            {
                var literal = relay.Bindings.OfType<LiteralToolArgumentBinding>().FirstOrDefault(b => b.Path == "/selection");
                if (literal is null) continue;
                DataSourceDocumentSelection? selection;
                try { selection = literal.Value.Deserialize<DataSourceDocumentSelection>(Json); } catch (JsonException) { continue; }
                if (selection?.SourceUid != sourceUid) continue;
                result.Add(new(new(AgentstrationToolProvider.ToolResourceName(version.Name), version.ScopeRef, version.Namespace),
                    Guid.Empty, "", relay.Version, null, selection.AcquisitionUid is { } uid && selection.AcquisitionId is { } id ? new(id, uid) : null, true, DisplayName: relay.DisplayName));
            }
        }
        return result;
    }
}
