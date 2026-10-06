using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Artifacts;

public sealed class ArtifactContentMaterializationGateway(
    ArtifactManagementService artifacts,
    RootFlowSubmissionService submissions,
    FlowService flows,
    FlowRunService runs,
    ICurrentRequestContext requestContext) : IArtifactContentMaterializationGateway
{
    public async Task<FlowRunArtifactMaterialization> StartAsync(
        FlowRunArtifactId artifactId,
        MaterializeFlowRunArtifactRequest request,
        CancellationToken cancellationToken)
    {
        _ = await artifacts.GetFlowRunArtifactForContentReadAsync(artifactId, true, cancellationToken)
            ?? throw Error("flow_run_artifact_not_found", $"FlowRunArtifact '{artifactId}' was not found.");
        var current = Current();
        var flowName = string.IsNullOrWhiteSpace(request.FlowName)
            ? ArtifactPlatformResourceProvisioner.StorageReadFlowName
            : request.FlowName.Trim();
        var flowNamespace = ResourceNamespace.Parse(request.FlowNamespace);
        var input = JsonSerializer.SerializeToElement(new { flowRunArtifactId = artifactId.ToString() });
        try
        {
            var requestedFlow = new FlowReference(new FlowId(flowName, flowNamespace), request.FlowVersion,
                string.IsNullOrWhiteSpace(request.FlowVersion), flowNamespace);
            var resolvedFlow = await flows.ResolveAsync(new WorkspaceId(current.WorkspaceId), requestedFlow,
                flowNamespace, cancellationToken);
            if (!resolvedFlow.Metadata.TryGetValue("artifact.contract", out var contract)
                || !string.Equals(contract, ArtifactFlowContracts.StorageRead, StringComparison.Ordinal))
                throw Error("artifact_storage_read_flow_contract_invalid",
                    $"Flow '{resolvedFlow.FlowId}' does not implement '{ArtifactFlowContracts.StorageRead}'.");
            var submission = await submissions.SubmitAsync(new SubmitRootFlowCommand(
                new WorkspaceId(current.WorkspaceId),
                new FlowReference(resolvedFlow.FlowId, resolvedFlow.Version, false, resolvedFlow.FlowId.Namespace),
                input,
                FlowInvocationOrigin.Console,
                current.PrincipalId.ToString("D"),
                FlowRunTrigger.Api,
                string.IsNullOrWhiteSpace(request.IdempotencyKey)
                    ? $"artifact-read:{artifactId}:{Guid.NewGuid():N}"
                    : request.IdempotencyKey.Trim(),
                artifactId.ToString(),
                $"artifact-read:{artifactId}",
                Type: "artifact-storage-read",
                Instruction: $"Materialize durable Artifact '{artifactId}'.",
                Title: $"Read durable Artifact {artifactId}"), cancellationToken);
            return Snapshot(submission.FlowRun.Run, artifactId);
        }
        catch (ArtifactValidationException) { throw; }
        catch (Exception exception) when (exception is FlowValidationException or FlowNotFoundException or WorkValidationException)
        {
            throw Error("artifact_storage_read_flow_rejected", exception.Message);
        }
    }

    public async Task<FlowRunArtifactMaterialization?> GetAsync(
        FlowRunArtifactId artifactId,
        string flowRunId,
        CancellationToken cancellationToken)
    {
        _ = await artifacts.GetFlowRunArtifactForContentReadAsync(artifactId, false, cancellationToken)
            ?? throw Error("flow_run_artifact_not_found", $"FlowRunArtifact '{artifactId}' was not found.");
        var current = Current();
        var stored = await runs.GetAsync(flowRunId,
            new FlowRunScope(current.TenantId, new WorkspaceId(current.WorkspaceId), current.PrincipalId), cancellationToken);
        if (stored is null) return null;
        if (!MatchesArtifact(stored.Value.Input, artifactId)) return null;
        return Snapshot(stored.Value, artifactId);
    }

    private RequestContext Current() => requestContext.IsInitialized
        ? requestContext.Current
        : throw new InvalidOperationException("Artifact materialization requires an authenticated Workspace context.");

    private static bool MatchesArtifact(JsonElement input, FlowRunArtifactId artifactId) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("flowRunArtifactId", out var value)
        && string.Equals(value.GetString(), artifactId.ToString(), StringComparison.Ordinal);

    private static FlowRunArtifactMaterialization Snapshot(FlowRun run, FlowRunArtifactId artifactId)
    {
        if (!MatchesArtifact(run.Input, artifactId))
            throw Error("artifact_storage_read_flow_mismatch", "The FlowRun does not materialize the requested durable Artifact.");
        StagedArtifactId? stagedArtifactId = null;
        if (run.Status == FlowRunStatus.Succeeded && run.Output is { ValueKind: JsonValueKind.Object } output
            && output.TryGetProperty("artifactReference", out var reference)
            && reference.ValueKind == JsonValueKind.String)
        {
            try { stagedArtifactId = StagedArtifactId.Parse(reference.GetString()!); }
            catch (FormatException) { }
        }
        return new(run.Id, run.Status.ToString(), stagedArtifactId, run.Error?.Code, run.Error?.Message);
    }

    private static ArtifactValidationException Error(string code, string message) => new(code, message);
}
