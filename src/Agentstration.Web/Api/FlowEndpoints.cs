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
    public static IEndpointRouteBuilder MapAgentstrationFlowApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/flows").RequireAuthorization(AgentstrationPolicies.Authenticated);
        group.MapPost("/", CreateAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{id}", GetAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/{id}", UpdateAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/{id}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        group.MapGet("/{id}/versions", ListVersionsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{id}/versions/{version}", GetVersionAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/{id}/versions", CreateVersionAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/{id}/runs", CreateRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        group.MapGet("/{id}/runs", ListFlowRunsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapGet("/{id}/runs/{runId}", GetFlowRunAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapPost("/{id}/runs/{runId}/cancel", CancelFlowRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        group.MapPost("/drafts", CreateDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/{id}/draft", GetDraftAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/{id}/draft", SaveDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/{id}/validate", ValidateDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/{id}/draft/source", GetDraftSourceAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/{id}/draft/source", ReplaceDraftSourceAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/{id}/publish", PublishDraftAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/{id}/draft/runs", CreateDraftRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        group.MapPost("/{id}/versions/{version}/draft", CreateDraftFromVersionAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        var namespaced = endpoints.MapGroup("/api/namespaces/{namespace}/flows").RequireAuthorization(AgentstrationPolicies.Authenticated);
        namespaced.MapPost("/", CreateNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        namespaced.MapGet("/", ListNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        namespaced.MapGet("/{id}", GetNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        namespaced.MapPut("/{id}", UpdateNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        namespaced.MapDelete("/{id}", DeleteNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        namespaced.MapGet("/{id}/versions", ListNamespacedVersionsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        namespaced.MapGet("/{id}/versions/{version}", GetNamespacedVersionAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        namespaced.MapPost("/{id}/runs", CreateNamespacedRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        namespaced.MapGet("/{id}/runs", ListNamespacedFlowRunsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        var runs = endpoints.MapGroup("/api/flowRuns").RequireAuthorization(AgentstrationPolicies.Authenticated);
        runs.MapGet("/", ListRunsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapGet("/{runId}", GetRunAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapDelete("/{runId}", DeleteRunAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteRuns);
        runs.MapGet("/{runId}/events", ObserveRunAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapGet("/{runId}/eventHistory", ListRunEventsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapGet("/{runId}/inputs", ListInputsAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapGet("/{runId}/inputs/{inputId}", GetInputAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        runs.MapPost("/{runId}/inputs/{inputId}/response", RespondToInputAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        runs.MapPost("/{runId}/cancel", CancelRunAsync).RequireAuthorization(AgentstrationPolicies.CanRunFlows);
        return endpoints;
    }
































































































    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

}
