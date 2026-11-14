using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.DataSources;

public interface IDataSourceDocumentToolResolver
{
    Task<ToolRelayTarget> ResolveAsync(ResourceScopeRef consumer, CancellationToken ct);
}

public sealed class DataSourceDocumentToolExposureService(DataSourceDocumentAccessService access,
    ToolDefinitionService definitions, IDataSourceDocumentToolResolver nativeTools,
    ICurrentRequestContext current, IRequestContextScopeFactory scopes, IAuthorizationService authorization)
{
    private readonly DataSourceExecutionContext execution = new(current, scopes);

    public async Task<ResourceReference> PublishAsync(ResourceNamespace ns, string acquisitionId,
        PublishDataSourceDocumentToolRequest request, CancellationToken ct)
    {
        await authorization.EnsurePermissionAsync(execution.RequireContext("data_source_context_required"), AuthorizationPermissions.ResourcesWrite, ct);
        if (request.Mode is not (DataSourceDocumentSelectionModes.Exact or DataSourceDocumentSelectionModes.Latest))
            throw new DataSourceValidationException("data_source_document_selection_invalid", "Choose a supported acquisition selection mode.");
        var description = await access.DescribeAsync(ns, acquisitionId, ct);
        if (request.Profile != description.Selection.Contract.Profile || !description.Documents.Policy.Query
            || !description.Documents.QueryProfiles.Contains(request.Profile))
            throw new DataSourceValidationException("data_source_document_contract_mismatch", "The exact query contract is unavailable.");
        var selection = request.Mode == DataSourceDocumentSelectionModes.Exact ? description.Selection
            : description.Selection with { Mode = DataSourceDocumentSelectionModes.Latest, AcquisitionId = null, AcquisitionUid = null };
        _ = await access.ResolveAsync(selection, ct); // Never publish a dynamic relay which already disagrees with the latest contract.
        var scope = execution.ConsumerScope;
        var name = "datasource-" + selection.SourceUid.ToString("N")
            + (request.Mode == DataSourceDocumentSelectionModes.Exact ? "." + description.AcquisitionUid.ToString("N") : "") + ".query";
        var previous = await definitions.GetExactAsync(name, ns, scope, ct);
        if (previous is not null && (!previous.Value.Metadata.Annotations.TryGetValue("agentstration.io/data-source-uid", out var owner)
            || owner != selection.SourceUid.ToString("D")))
            throw new DataSourceValidationException("data_source_document_tool_conflict", "An unrelated ToolDefinition owns this name.");
        List<ToolArgumentBinding> bindings =
        [
            new LiteralToolArgumentBinding { Path = "/selection", Value = JsonSerializer.SerializeToElement(selection, JsonSerializerOptions.Web) },
            new LiteralToolArgumentBinding { Path = "/request/profile", Value = JsonSerializer.SerializeToElement(request.Profile, JsonSerializerOptions.Web) }
        ];
        if (request.Mode == DataSourceDocumentSelectionModes.Exact)
            bindings.Add(new LiteralToolArgumentBinding { Path = "/request/acquisitionId", Value = JsonSerializer.SerializeToElement(description.AcquisitionId) });
        try
        {
            _ = await definitions.PutAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolDefinition, ScopeRef = scope,
                Metadata = new()
                {
                    Name = name, Namespace = ns,
                    Annotations = new Dictionary<string, string>
                    {
                        ["agentstration.io/data-source-uid"] = selection.SourceUid.ToString("D"),
                        ["agentstration.io/acquisition-scope"] = selection.AcquisitionScope.Value,
                        ["agentstration.io/acquisition-selection"] = request.Mode
                    }
                },
                Definition = new RelayToolDefinitionProperties
                {
                    Version = request.Version, Publish = true, DisplayName = selection.Source.Name + " query",
                    Description = request.Mode == DataSourceDocumentSelectionModes.Latest
                        ? "Query the latest successful retained acquisition of this Data Source, or provide acquisitionId for an exact retained acquisition. Source, stream, schema and query profile are fixed. Continue with cursor only; pagination stays on its first acquisition. No acquisition is started and no physical destination argument is accepted."
                        : "Query one exact immutable Data Source acquisition. Source, acquisition identity, schema and query profile are fixed. Continue with cursor only. No acquisition is started.",
                    RequiresApproval = request.RequiresApproval, Target = await nativeTools.ResolveAsync(scope, ct), Bindings = bindings
                }
            }, previous?.ETag, previous is null, ct);
        }
        catch (ToolDefinitionValidationException exception)
        { throw new DataSourceValidationException(exception.Code, "The acquisition query Tool publication is invalid."); }
        return new(AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName(name, request.Version)), scope, ns);
    }
}
