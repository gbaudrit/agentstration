using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Artifacts;

public sealed class ArtifactStorageWriteMcpTool(
    ArtifactManagementService artifacts,
    IArtifactDurableStore durable) : ArtifactBrokerInternalTool(artifacts)
{
    public const string Name = "artifact.storage.local.write";

    public override InternalMcpToolDefinition Definition { get; } = new(Name, "Persist Artifact to local storage",
        "Deterministically copies sealed staged content to the built-in durable filesystem store and returns a normalized receipt.",
        Schema(new { type = "object", properties = new { stagedArtifactId = new { type = "string" }, producerFlowRunId = new { type = "string" }, producerFlowStepId = new { type = "string" }, storageFlowRunId = new { type = "string" } }, required = new[] { "stagedArtifactId", "producerFlowRunId", "producerFlowStepId" }, additionalProperties = false }), InitialCategory: new("artifact-storage", "Artifact storage", "Storage Flow implementation Tools."), ExposeThroughMcp: false);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var sourceId = StagedArtifactId.Parse(RequiredString(invocation.Arguments, "stagedArtifactId"));
        var source = await Artifacts.GetStagedAsync(sourceId, null, ArtifactLeaseOperation.Inspect, cancellationToken)
            ?? throw Error("staged_artifact_not_found", "The staged Artifact was not found.");
        if (source.Value.ArtifactStatus is not (StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted))
            throw Error("staged_artifact_not_sealed", "Only sealed staged content can be persisted.");
        var storageFlowRunId = invocation.RunId ?? OptionalString(invocation.Arguments, "storageFlowRunId") ?? invocation.CallId;
        var opaqueReference = StableReference(invocation.WorkspaceId, sourceId, storageFlowRunId);
        await durable.ResetAsync(invocation.WorkspaceId, opaqueReference, cancellationToken);
        long offset = 0;
        while (offset < source.Value.Length)
        {
            var length = checked((int)Math.Min(ArtifactManagementService.MaximumChunkBytes, source.Value.Length - offset));
            var chunk = await Artifacts.ReadAsync(sourceId, offset, length, null, cancellationToken);
            var bytes = Convert.FromBase64String(chunk.ContentBase64);
            offset = await durable.WriteAsync(invocation.WorkspaceId, opaqueReference, offset, bytes, cancellationToken);
        }
        var stat = await durable.StatAsync(invocation.WorkspaceId, opaqueReference, cancellationToken);
        var receipt = new ArtifactStorageReceipt
        {
            StorageFlowRunId = storageFlowRunId,
            OpaqueReference = opaqueReference,
            MediaType = source.Value.MediaType,
            Length = stat.Length,
            Sha256 = stat.Sha256,
            Provenance = new Dictionary<string, string>
            {
                ["provider"] = "filesystem",
                ["sourceStagedArtifactId"] = sourceId.ToString(),
                ["fileName"] = source.Value.FileName,
                ["correlationId"] = invocation.CorrelationId ?? string.Empty
            }
        };
        var completed = await Artifacts.CompleteFlowRunArtifactAsync(sourceId, new(
            RequiredString(invocation.Arguments, "producerFlowRunId"),
            RequiredString(invocation.Arguments, "producerFlowStepId"), receipt), cancellationToken);
        return JsonSerializer.SerializeToElement(new { flowRunArtifactId = completed.Value.ArtifactId.ToString(), receipt });
    }

    private static string StableReference(WorkspaceId workspaceId, StagedArtifactId artifactId, string storageFlowRunId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{workspaceId.Value:N}:{artifactId}:{storageFlowRunId}"));
        return Convert.ToHexStringLower(bytes);
    }
}

public sealed class ArtifactStorageReadMcpTool(
    ArtifactManagementService artifacts,
    IArtifactDurableStore durable) : ArtifactBrokerInternalTool(artifacts)
{
    public const string Name = "artifact.storage.local.read";

    public override InternalMcpToolDefinition Definition { get; } = new(Name, "Materialize Artifact from local storage",
        "Materializes durable content into a new governed staged Artifact without exposing the durable backend reference.",
        Schema(new { type = "object", properties = new { flowRunArtifactId = new { type = "string" }, binding = new { type = "string" }, expiresAt = new { type = "string" } }, required = new[] { "flowRunArtifactId" }, additionalProperties = false }), InitialCategory: new("artifact-storage", "Artifact storage", "Storage Flow implementation Tools."), ExposeThroughMcp: false);

    public override async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var durableArtifact = await Artifacts.GetFlowRunArtifactAsync(
            FlowRunArtifactId.Parse(RequiredString(invocation.Arguments, "flowRunArtifactId")), cancellationToken)
            ?? throw Error("flow_run_artifact_not_found", "The FlowRunArtifact was not found.");
        var receipt = durableArtifact.Value.Receipt;
        var binding = OptionalString(invocation.Arguments, "binding");
        var producer = new ArtifactProducer
        {
            Kind = ArtifactProducerKind.FlowRun,
            Id = invocation.RunId ?? invocation.CallId,
            FlowRunId = invocation.RunId,
            FlowStepId = invocation.FlowStepId,
            ToolCallId = invocation.CallId,
            CorrelationId = invocation.CorrelationId
        };
        var staged = await Artifacts.CreateStagedAsync(new(
            receipt.Provenance.TryGetValue("fileName", out var fileName) ? fileName : $"{durableArtifact.Value.ArtifactId}.bin",
            receipt.MediaType,
            producer,
            binding is null ? null : new ResourceReference(binding),
            OptionalTimestamp(invocation.Arguments, "expiresAt")), cancellationToken);
        long offset = 0;
        while (offset < receipt.Length)
        {
            var length = checked((int)Math.Min(ArtifactManagementService.MaximumChunkBytes, receipt.Length - offset));
            var bytes = await durable.ReadAsync(invocation.WorkspaceId, receipt.OpaqueReference, offset, length, cancellationToken);
            var updated = await Artifacts.WriteAsync(staged.Value.ArtifactId, offset, bytes, cancellationToken);
            offset = updated.Value.Length;
        }
        var sealedArtifact = await Artifacts.SealAsync(staged.Value.ArtifactId, cancellationToken);
        if (sealedArtifact.Value.Length != receipt.Length
            || !string.Equals(sealedArtifact.Value.Sha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("artifact_storage_integrity_mismatch", "Materialized content does not match the durable storage receipt.");
        return View(sealedArtifact.Value);
    }
}
