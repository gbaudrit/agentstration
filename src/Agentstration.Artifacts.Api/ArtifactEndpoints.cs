using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Artifacts.Api.Internal;

internal static class ArtifactEndpoints
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static void Map(RouteGroupBuilder artifacts)
    {
        artifacts.RequireAuthorization(AgentstrationPolicies.Authenticated);
        artifacts.MapGet("/staging-bindings", ListBindingsAsync).WithSummary("List Artifact staging bindings").Produces<IEnumerable<ArtifactStagingBindingResource>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapGet("/staging-bindings/{name}", GetBindingAsync).WithSummary("Get an Artifact staging binding").Produces<ArtifactStagingBindingResource>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapPost("/staging-bindings", CreateBindingAsync).WithSummary("Create an Artifact staging binding").Produces<ArtifactStagingBindingResource>(StatusCodes.Status201Created).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        artifacts.MapGet("/staged", ListStagedAsync).WithSummary("List staged Artifacts").Produces<IEnumerable<StagedArtifactView>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapPost("/staged", CreateStagedAsync).WithSummary("Create a staged Artifact").Produces<StagedArtifactView>(StatusCodes.Status201Created).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        artifacts.MapGet("/staged/{id}", GetStagedAsync).WithSummary("Inspect a staged Artifact").Produces<StagedArtifactView>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapPost("/staged/{id}/content", WriteAsync).WithSummary("Write a bounded staged Artifact chunk").Produces<StagedArtifactView>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        artifacts.MapGet("/staged/{id}/content", ReadAsync).WithSummary("Read a bounded staged Artifact chunk").Produces<ArtifactContentChunk>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapGet("/staged/{id}/download", DownloadAsync).WithSummary("Download governed staged Artifact content").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapPost("/staged/{id}/seal", SealAsync).WithSummary("Seal and verify a staged Artifact").Produces<StagedArtifactView>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        artifacts.MapPost("/staged/{id}/leases", CreateLeaseAsync).WithSummary("Create a scoped staged Artifact lease").Produces<StagedArtifactView>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        artifacts.MapPost("/staged/{id}/handoffs", HandoffAsync).WithSummary("Transfer or delegate a staged Artifact").Produces<ArtifactHandoffResult>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        artifacts.MapDelete("/staged/{id}/leases/{leaseId}", RevokeLeaseAsync).WithSummary("Revoke a staged Artifact lease").Produces<StagedArtifactView>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        artifacts.MapPost("/staged/{id}/retention", ExtendRetentionAsync).WithSummary("Extend staged Artifact retention").Produces<StagedArtifactView>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        artifacts.MapDelete("/staged/{id}", PurgeAsync).WithSummary("Purge staged Artifact content").Produces(StatusCodes.Status204NoContent).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        artifacts.MapGet("/flow-run-artifacts", ListFlowRunArtifactsAsync).WithSummary("List durable FlowRunArtifacts").Produces<IEnumerable<FlowRunArtifactResource>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapGet("/flow-run-artifacts/{id}", GetFlowRunArtifactAsync).WithSummary("Get a durable FlowRunArtifact").Produces<FlowRunArtifactResource>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapPost("/flow-run-artifacts/{id}/materializations", StartMaterializationAsync).WithSummary("Materialize durable Artifact content through a Storage Read Flow").Produces<FlowRunArtifactMaterialization>(StatusCodes.Status202Accepted).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapGet("/flow-run-artifacts/{id}/materializations", ListMaterializationsAsync).WithSummary("List durable Artifact materializations").Produces<IEnumerable<FlowRunArtifactMaterialization>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapGet("/flow-run-artifacts/{id}/materializations/{flowRunId}", GetMaterializationAsync).WithSummary("Get durable Artifact materialization status").Produces<FlowRunArtifactMaterialization>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        artifacts.MapPost("/staged/{id}/flow-run-artifacts", CompleteAsync).WithSummary("Complete a durable FlowRunArtifact from a storage receipt").Produces<FlowRunArtifactResource>(StatusCodes.Status201Created).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
    }

    private static Task<IResult> ListBindingsAsync(ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok((await service.ListBindingsAsync(token)).Select(value => value.Value)));

    private static Task<IResult> CreateBindingAsync(CreateArtifactStagingBindingRequest body, HttpResponse response,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateBindingAsync(ResourceNamespace.Parse(body.Namespace), body.Name, body.Properties, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/artifacts/staging-bindings/{Uri.EscapeDataString(body.Name)}";
        return Results.Json(stored.Value, statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> GetBindingAsync(string name, string? @namespace,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetBindingAsync(ResourceNamespace.Parse(@namespace), name, token);
        return stored is null ? Results.NotFound() : Results.Ok(stored.Value);
    });

    private static Task<IResult> ListStagedAsync(ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok((await service.ListStagedAsync(token)).Select(value => ArtifactViews.Staged(value.Value))));

    private static Task<IResult> GetStagedAsync(string id, string? leaseId, ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetStagedAsync(StagedArtifactId.Parse(id), ParseLease(leaseId), ArtifactLeaseOperation.Inspect, token);
        return stored is null ? Results.NotFound() : Results.Ok(ArtifactViews.Staged(stored.Value));
    });

    private static Task<IResult> CreateStagedAsync(CreateStagedArtifactRequest body, HttpResponse response,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CreateStagedAsync(body, token);
        response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/artifacts/staged/{stored.Value.ArtifactId}";
        return Results.Json(ArtifactViews.Staged(stored.Value), statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> WriteAsync(string id, WriteStagedArtifactRequest body,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var content = Convert.FromBase64String(body.ContentBase64);
        var stored = await service.WriteAsync(StagedArtifactId.Parse(id), body.Offset, content, token);
        return Results.Ok(ArtifactViews.Staged(stored.Value));
    });

    private static Task<IResult> ReadAsync(string id, long offset, int length, string? leaseId,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok(await service.ReadAsync(StagedArtifactId.Parse(id), offset, length, ParseLease(leaseId), token)));

    private static Task<IResult> DownloadAsync(string id, string? leaseId,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var artifactId = StagedArtifactId.Parse(id);
        var parsedLeaseId = ParseLease(leaseId);
        var stored = await service.GetStagedAsync(artifactId, parsedLeaseId, ArtifactLeaseOperation.Read, token);
        return stored is null
            ? Results.NotFound()
            : Results.Stream(
                new ArtifactReadStream(service, stored.Value, parsedLeaseId),
                stored.Value.MediaType,
                stored.Value.FileName,
                enableRangeProcessing: true);
    });

    private static Task<IResult> SealAsync(string id, ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok(ArtifactViews.Staged((await service.SealAsync(StagedArtifactId.Parse(id), token)).Value)));

    private static Task<IResult> CreateLeaseAsync(string id, CreateArtifactLeaseRequest body,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok(ArtifactViews.Staged((await service.CreateLeaseAsync(StagedArtifactId.Parse(id), body, token)).Value)));

    private static Task<IResult> RevokeLeaseAsync(string id, string leaseId,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok(ArtifactViews.Staged((await service.RevokeLeaseAsync(
            StagedArtifactId.Parse(id), ArtifactLeaseId.Parse(leaseId), token)).Value)));

    private static Task<IResult> HandoffAsync(string id, ArtifactHandoffRequest body,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok(await service.HandoffAsync(StagedArtifactId.Parse(id), body, token)));

    private static Task<IResult> ExtendRetentionAsync(string id, ExtendArtifactRetentionRequest body,
        ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok(ArtifactViews.Staged((await service.ExtendRetentionAsync(StagedArtifactId.Parse(id), body.ExpiresAt, token)).Value)));

    private static Task<IResult> PurgeAsync(string id, ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        await service.PurgeAsync(StagedArtifactId.Parse(id), token);
        return Results.NoContent();
    });

    private static Task<IResult> ListFlowRunArtifactsAsync(ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
        Results.Ok((await service.ListFlowRunArtifactsAsync(token)).Select(value => value.Value)));

    private static Task<IResult> GetFlowRunArtifactAsync(string id, ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetFlowRunArtifactAsync(FlowRunArtifactId.Parse(id), token);
        return stored is null ? Results.NotFound() : Results.Ok(stored.Value);
    });

    private static Task<IResult> StartMaterializationAsync(string id, MaterializeFlowRunArtifactRequest body,
        HttpResponse response, IArtifactContentMaterializationGateway gateway, CancellationToken token) => ExecuteAsync(async () =>
    {
        var artifactId = FlowRunArtifactId.Parse(id);
        var materialization = await gateway.StartAsync(artifactId, body, token);
        var location = $"/api/artifacts/flow-run-artifacts/{artifactId}/materializations/{Uri.EscapeDataString(materialization.FlowRunId)}";
        response.Headers.Location = location;
        return Results.Accepted(location, materialization);
    });

    private static Task<IResult> GetMaterializationAsync(string id, string flowRunId,
        IArtifactContentMaterializationGateway gateway, CancellationToken token) => ExecuteAsync(async () =>
    {
        var materialization = await gateway.GetAsync(FlowRunArtifactId.Parse(id), flowRunId, token);
        return materialization is null ? Results.NotFound() : Results.Ok(materialization);
    });

    private static Task<IResult> ListMaterializationsAsync(string id, int? maximum,
        IArtifactContentMaterializationGateway gateway, CancellationToken token) => ExecuteAsync(async () =>
    {
        var materializations = await gateway.ListAsync(FlowRunArtifactId.Parse(id), maximum ?? 50, token);
        return Results.Ok(materializations);
    });

    private static Task<IResult> CompleteAsync(string id, CompleteFlowRunArtifactRequest body,
        HttpResponse response, ArtifactManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.CompleteFlowRunArtifactAsync(StagedArtifactId.Parse(id), body, token);
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: StatusCodes.Status201Created);
    });

    private static ArtifactLeaseId? ParseLease(string? value) => string.IsNullOrWhiteSpace(value) ? null : ArtifactLeaseId.Parse(value);

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AuthorizationDeniedException exception) { return Problem("artifact-access-denied", "Artifact access denied", 403, exception.Message); }
        catch (ArtifactValidationException exception) { return Problem(exception.Code, "Invalid Artifact operation", 422, exception.Message); }
        catch (ResourceConcurrencyException exception) { return Problem("artifact-version-conflict", "Artifact version conflict", 409, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        { return Problem("artifact-request-invalid", "Invalid Artifact request", 400, exception.Message); }
    }

    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    {
        Type = ProblemBase + type,
        Title = title,
        Status = status,
        Detail = detail
    });

    private sealed class ArtifactReadStream(
        ArtifactManagementService service,
        StagedArtifactResource artifact,
        ArtifactLeaseId? leaseId) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => artifact.Length;

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0 || position >= Length)
                return 0;

            var length = checked((int)Math.Min(
                Math.Min(buffer.Length, ArtifactManagementService.MaximumChunkBytes),
                Length - position));
            var chunk = await service.ReadAsync(artifact.ArtifactId, position, length, leaseId, cancellationToken);
            var content = Convert.FromBase64String(chunk.ContentBase64);
            if (content.Length == 0)
                throw new ArtifactValidationException("staged_artifact_download_no_progress", "The Artifact backend returned no content before the expected end.");
            if (content.Length > buffer.Length || position + content.Length > Length)
                throw new ArtifactValidationException("staged_artifact_download_invalid_chunk", "The Artifact backend returned content outside the requested range.");

            content.CopyTo(buffer);
            position += content.Length;
            return content.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadArrayAsync(buffer.AsMemory(offset, count), cancellationToken);

        private async Task<int> ReadArrayAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            await ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0 || target > Length)
                throw new IOException("Cannot seek outside the Artifact content bounds.");
            position = target;
            return position;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
