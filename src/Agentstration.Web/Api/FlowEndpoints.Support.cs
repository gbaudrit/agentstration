using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static partial class FlowEndpoints
{
    private static async Task<StoredFlow> RequiredAsync(WorkspaceId workspaceId, string id, FlowService service, CancellationToken token) =>
        await service.GetAsync(workspaceId, new FlowId(id), token) ?? throw new FlowNotFoundException(new FlowId(id));

    private static WorkspaceId CurrentWorkspace(ICurrentRequestContext requestContext) => CurrentScope(requestContext).WorkspaceId;

    private static FlowResponse ToResponse(FlowResource value) => new(value.Id.Value, value.Name, value.Description, value.Version, value.Enabled, value.ActiveVersion, value.Definition, value.Metadata, value.CreatedAt, value.UpdatedAt, value.Graph) { Namespace = value.Id.Namespace };

    private static FlowSummaryResponse ToSummary(FlowResource value) => new(value.Id.Value, value.Name, value.Description, value.Definition.Kind, value.Version, value.Enabled, value.ActiveVersion, value.UpdatedAt) { Namespace = value.Id.Namespace };

    private static FlowVersionResponse ToVersion(FlowVersion value) => new(value.FlowId.Value, value.Version, value.Description, value.Definition, value.Metadata, value.PublishedAt, value.Graph, value.DefinitionHash, value.ReleaseNotes) { Namespace = value.FlowId.Namespace };

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (FlowNotFoundException exception) { return Results.Problem(statusCode: 404, title: "flow_not_found", detail: exception.Message); }
        catch (FlowRunNotFoundException exception) { return Results.Problem(statusCode: 404, title: "flow_run_not_found", detail: exception.Message); }
        catch (InputRequestAlreadyResolvedException exception) { return Results.Problem(statusCode: 409, title: "input_request_already_resolved", detail: exception.Message); }
        catch (FlowConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (FlowValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
    }

    private static string RequiredIfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault()
        ?? throw new FlowValidationException("if_match_required", "Saving a Flow Draft requires an If-Match ETag.");
}
