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
using Microsoft.AspNetCore.DataProtection;

namespace Agentstration.Infrastructure.DataSources;

public sealed class DataSourceDocumentCursorProtector(IDataProtectionProvider provider) : IDataSourceDocumentCursorProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Agentstration.DataSource.DocumentQuery.v1");
    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}

public sealed class DataSourceDocumentToolResolver(InternalMcpToolProjectionService projection, IResourceStore resources)
    : IDataSourceDocumentToolResolver
{
    public async Task<ToolRelayTarget> ResolveAsync(ResourceScopeRef consumer, CancellationToken ct)
    {
        var scope = consumer.Kind == ResourceScopeKind.Workspace ? consumer : ResourceScopeRef.Instance;
        await projection.EnsureAsync(scope, ResourceNamespace.Default, ct);
        var name = AgentstrationToolProvider.ToolResourceName(DataSourceDocumentQueryMcpTool.Name);
        var tool = (await resources.GetExactAsync<ToolResource>(new(scope, ResourceNamespace.Default, ToolResourceKinds.Tool, name), ct))?.Value
            ?? throw new DataSourceValidationException("data_source_document_tool_unavailable", "The platform Data Source query Tool is unavailable.");
        return new() { Name = name, ScopeRef = scope, Namespace = ResourceNamespace.Default, Uid = tool.Uid, Generation = tool.Generation };
    }
}

public sealed class DataSourceDocumentQueryMcpTool(DataSourceDocumentAccessService access, ICurrentRequestContext current) : IInternalMcpToolHandler
{
    public const string Name = "datasource.documents.query";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public InternalMcpToolDefinition Definition => DataSourceDocumentQueryDefinitionProvider.Value;

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration)
            || current.Current.PrincipalId != invocation.PrincipalId || current.Current.TenantId != invocation.TenantId
            || current.Current.WorkspaceId != invocation.WorkspaceId.Value)
            throw new ToolDefinitionInvocationException("data_source_document_context_mismatch", "The query caller context does not match.");
        try
        {
            var selection = invocation.Arguments.GetProperty("selection").Deserialize<DataSourceDocumentSelection>(Json) ?? throw new JsonException();
            var request = invocation.Arguments.GetProperty("request").Deserialize<DataSourceDocumentQueryRequest>(Json) ?? throw new JsonException();
            return JsonSerializer.SerializeToElement(await access.QueryAsync(selection, request, cancellationToken), Json);
        }
        catch (DataSourceValidationException exception) { throw new ToolDefinitionInvocationException(exception.Code, "The acquisition document query failed."); }
        catch (DataSourceAcquisitionException exception) { throw new ToolDefinitionInvocationException(exception.Code, "The acquisition document query failed."); }
        catch (AuthorizationDeniedException) { throw new ToolDefinitionInvocationException("data_source_document_access_denied", "The acquisition query is not authorized."); }
        catch (ResourceScopeAccessDeniedException) { throw new ToolDefinitionInvocationException("data_source_document_selection_forbidden", "The acquisition query is not authorized."); }
        catch (StorageException exception) { throw new ToolDefinitionInvocationException(exception.Code, "The acquisition document query failed."); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException)
        { throw new ToolDefinitionInvocationException("data_source_document_query_invalid", "The acquisition query is invalid."); }
    }
}

// Definitions must be enumerable during bootstrap without constructing Flow execution services.
public sealed class DataSourceDocumentQueryDefinitionProvider : IInternalMcpToolDefinitionProvider
{
    public InternalMcpToolDefinition Definition => Value;
    internal static InternalMcpToolDefinition Value { get; } = new(DataSourceDocumentQueryMcpTool.Name, "Query Data Source acquisition documents",
        "Governed read-only acquisition query. A relay fixes the source, acquisition stream and versioned query contract. Latest mode accepts an optional acquisitionId; exact mode fixes the acquisition. Cursors stay on the first selected acquisition. Requires current Source Use, runs/read and runs/execute permissions.",
        JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"selection":{"type":"object"},"request":{"type":"object","properties":{
              "profile":{"type":"object","properties":{"id":{"type":"string"},"version":{"type":"string"},"encoding":{"type":"string"},"modelDigest":{"type":"string"}},"required":["id","version","encoding","modelDigest"],"additionalProperties":false},
              "query":{"type":"object"},"pageSize":{"type":"integer","minimum":1,"maximum":100},
              "acquisitionId":{"type":"string","maxLength":128},"cursor":{"type":"string","maxLength":32768}},
              "required":["profile"],"additionalProperties":false}},"required":["selection","request"],"additionalProperties":false}
            """), JsonSerializer.SerializeToElement(new { type = "object" }), ExposeThroughMcp: false);

}
