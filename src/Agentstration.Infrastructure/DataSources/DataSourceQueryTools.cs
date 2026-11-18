using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Notifications;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.DataSources;

public sealed class DataSourceQueryToolResolver(InternalMcpToolProjectionService projection, IResourceStore resources) : IDataSourceQueryToolResolver
{
    public async Task<ToolRelayTarget> ResolveAsync(ResourceScopeRef consumer, CancellationToken ct)
    {
        var scope = consumer.Kind == ResourceScopeKind.Workspace ? consumer : ResourceScopeRef.Instance;
        await projection.EnsureAsync(scope, ResourceNamespace.Default, ct);
        var name = AgentstrationToolProvider.ToolResourceName(DataSourceQueryToolDefinitionProvider.Name);
        var tool = (await resources.GetExactAsync<ToolResource>(new(scope, ResourceNamespace.Default, ToolResourceKinds.Tool, name), ct))!.Value;
        return new() { Name = name, ScopeRef = scope, Namespace = ResourceNamespace.Default, Uid = tool.Uid, Generation = tool.Generation };
    }
}

public sealed class DataSourceQueryToolDefinitionProvider : IInternalMcpToolDefinitionProvider
{
    public const string Name = "datasource.queries.execute";
    public InternalMcpToolDefinition Definition => Value;
    internal static InternalMcpToolDefinition Value { get; } = new(Name, "Execute a published Data Source Query",
        "Execute a named Query. Omit its version to resolve the latest publication at the beginning of the call. Requires current Query/Source Use and execution rights. A relay may fix the Query, contract and acquisition. Continue with request.cursor only; the resolved Query version and acquisition stay fixed during pagination. Starts no acquisition.",
        JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{
          "query":{"type":"object","properties":{
            "query":{"type":"object","properties":{"name":{"type":"string"},"scopeRef":{"type":"string"},"namespace":{"type":"string"}},"required":["name","scopeRef","namespace"],"additionalProperties":false},
            "queryUid":{"type":"string"},"version":{"type":"string","maxLength":32}},"required":["query","queryUid"],"additionalProperties":false},
          "contract":{"type":"string"},
          "acquisition":{"type":"object","properties":{"id":{"type":"string","maxLength":128},"uid":{"type":"string"}},"required":["id","uid"],"additionalProperties":false},
          "request":{"type":"object","properties":{"pageSize":{"type":"integer","minimum":1,"maximum":100},"acquisitionId":{"type":"string","maxLength":128},"cursor":{"type":"string","maxLength":65536}},"additionalProperties":false}
        },"required":["query"],"additionalProperties":false}
        """), JsonSerializer.SerializeToElement(new { type = "object" }), ExposeThroughMcp: false);
}

public sealed class DataSourceQueryMcpTool(DataSourceQueryService queries, ICurrentRequestContext current) : IInternalMcpToolHandler
{
    public InternalMcpToolDefinition Definition => DataSourceQueryToolDefinitionProvider.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken ct)
    {
        if (current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration)
            || current.Current.PrincipalId != invocation.PrincipalId || current.Current.TenantId != invocation.TenantId
            || current.Current.WorkspaceId != invocation.WorkspaceId.Value)
            throw new ToolDefinitionInvocationException("data_source_document_context_mismatch", "The query caller context does not match.");
        try
        {
            var input = invocation.Arguments;
            var query = input.GetProperty("query").Deserialize<DataSourceQueryReference>(Json) ?? throw new JsonException();
            var request = input.TryGetProperty("request", out var body) ? body.Deserialize<DataSourceQueryExecutionRequest>(Json) ?? throw new JsonException() : new();
            var acquisition = input.TryGetProperty("acquisition", out var fixedAcquisition) ? fixedAcquisition.Deserialize<DataSourceQueryAcquisitionPin>(Json) : null;
            var contract = input.TryGetProperty("contract", out var expected) ? expected.GetString() : null;
            return JsonSerializer.SerializeToElement(await queries.ExecuteAsync(query, request, contract, acquisition, ct), Json);
        }
        catch (DataSourceValidationException e) { throw new ToolDefinitionInvocationException(e.Code, "The published Data Source Query failed."); }
        catch (DataSourceAcquisitionException e) { throw new ToolDefinitionInvocationException(e.Code, "The acquisition is unavailable."); }
        catch (StorageException e) { throw new ToolDefinitionInvocationException(e.Code, "The Query storage operation failed."); }
        catch (AuthorizationDeniedException) { throw new ToolDefinitionInvocationException("data_source_query_access_denied", "The Query is not authorized."); }
        catch (ResourceScopeAccessDeniedException) { throw new ToolDefinitionInvocationException("data_source_query_access_denied", "The Query scope is not authorized."); }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        { throw new ToolDefinitionInvocationException("data_source_query_invalid", "The Query input is invalid."); }
    }
}
