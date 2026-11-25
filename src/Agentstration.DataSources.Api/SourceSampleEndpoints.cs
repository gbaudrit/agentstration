using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.DataSources.Api;

internal static class SourceSampleEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var samples = endpoints.MapGroup("/api/source-samples").RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        samples.MapGet("/consumed-candidates", async (string name, string scopeRef, string? @namespace, SourceSampleService service, CancellationToken ct) =>
            await Execute(async () => Results.Ok(await service.CandidatesAsync(new(name, ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(@namespace)), ct)))).Produces<IReadOnlyList<ConsumedSampleCandidate>>().WithSummary("List retained acquisition candidates for authoring");
        samples.MapPost("/prepared-source", async (PrepareDataSourceSampleRequest body, SourceSampleService service, CancellationToken ct) =>
            await Execute(async () => Results.Ok(await service.PrepareDataSourceAsync(body, ct)))).Produces<SourceSample>().WithSummary("Prepare a Data Source authoring input from a raw sample");
        samples.MapPost("/consumed", async (PrepareConsumedSampleRequest body, SourceSampleService service, CancellationToken ct) =>
            await Execute(async () => Results.Ok(await service.PrepareConsumedAsync(body, ct)))).Produces<SourceSample>().WithSummary("Prepare an authoring input from a retained Data Source acquisition");
        samples.MapPost("/", async (AcquireSourceSampleRequest body, SourceSampleService service, CancellationToken ct) =>
            await Execute(async () => { var value = await service.AcquireAsync(body, ct); return Results.Accepted($"/api/source-samples/{value.Id}", value); })).Produces<SourceSample>(StatusCodes.Status202Accepted).WithSummary("Acquire an ephemeral raw operation sample");
        samples.MapGet("/{id}", async (string id, SourceSampleService service, CancellationToken ct) =>
            await Execute(async () => Results.Ok(await service.GetAsync(id, ct)))).Produces<SourceSample>().WithSummary("Read an authorized authoring sample");
        samples.MapPost("/{id}/cancel", async (string id, SourceSampleService service, CancellationToken ct) =>
            await Execute(async () => { await service.CancelAsync(id, ct); return Results.NoContent(); })).Produces(StatusCodes.Status204NoContent).WithSummary("Cancel a sample acquisition");
        samples.MapDelete("/{id}", async (string id, SourceSampleService service, CancellationToken ct) =>
            await Execute(async () => { await service.DeleteAsync(id, ct); return Results.NoContent(); })).Produces(StatusCodes.Status204NoContent).WithSummary("Delete an authoring sample");
        samples.MapPost("/{id}/preview", async (string id, ReplaySourceSampleRequest body, SourceSampleService service, CancellationToken ct) =>
            await Execute(async () => Results.Ok(await service.ReplayAsync(id, body, ct)))).Produces<SourceSequencePreview>().WithSummary("Replay a source sequence on an existing sample");
        endpoints.MapPost("/api/source-sequences/contracts", async (SourceSequenceRequest body, ISourceSequenceService service,
            ICurrentRequestContext current, CancellationToken ct) => await Execute(async () =>
            {
                var consumer = current.AccessMode switch
                {
                    ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
                    ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.Current.TenantId),
                    ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(current.Current.WorkspaceId),
                    _ => throw new DataSourceAcquisitionException("source_sample_context_required", "An authorized authoring context is required.")
                };
                if (body.Consumer != consumer) return Results.Forbid();
                return Results.Ok(await service.AnalyzeAsync(body, ct));
            })).RequireAuthorization(AgentstrationPolicies.CanReadResources).Produces<SourceSequenceAnalysis>().WithSummary("Resolve source sequence contracts for authoring");
    }
    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (DataSourceAcquisitionException error) { return Results.Problem(statusCode: error.Code == "source_sample_unavailable" ? 404 : 422, title: error.Code); }
        catch (DataSourceValidationException error) { return Results.Problem(statusCode: 422, title: error.Code); }
        catch (Agentstration.ConnectedServices.ConnectedServiceOperationDiscoveryException error) { return Results.Problem(statusCode: 422, title: error.Code); }
        catch (FlowValidationException error) { return Results.Problem(statusCode: 422, title: error.Code); }
        catch (Agentstration.Functions.Abstractions.TransformationFunctionException error) { return Results.Problem(statusCode: 422, title: error.Code); }
        catch (Agentstration.Schemas.Contracts.SchemaBindingResolutionException error) { return Results.Problem(statusCode: 422, title: error.Code); }
    }
}
