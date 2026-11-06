using System.Text;
using Agentstration.Artifacts.Contracts;

namespace Agentstration.Artifacts;

public sealed record DurableArtifactTextChunk(string ArtifactId, string MaterializationRunId,
    string MediaType, long TotalBytes, long Offset, long NextOffset, bool EndOfContent, string Text);

public sealed class DurableArtifactContentReader(
    ArtifactManagementService artifacts,
    IArtifactContentMaterializationGateway materializations,
    TimeProvider timeProvider)
{
    public const int MaximumTextBytes = 64 * 1024;

    public async Task<DurableArtifactTextChunk> ReadAsync(FlowRunArtifactId artifactId, string? materializationRunId,
        long offset, int length, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (offset < 0 || length is < 4 or > MaximumTextBytes)
            throw Error("durable_artifact_read_range_invalid", $"Offset must be nonnegative and length between 4 and {MaximumTextBytes} bytes.");
        var artifact = await artifacts.GetFlowRunArtifactForContentReadAsync(artifactId, true, cancellationToken)
            ?? throw Error("flow_run_artifact_not_found", "The durable Artifact was not found in the current execution scope.");
        if (offset > artifact.Value.Receipt.Length)
            throw Error("durable_artifact_read_range_invalid", "Offset exceeds the Artifact length.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        try
        {
            var materialized = string.IsNullOrWhiteSpace(materializationRunId)
                ? await materializations.StartAsync(artifactId, new(IdempotencyKey: idempotencyKey), token)
                : await materializations.GetAsync(artifactId, materializationRunId, token)
                    ?? throw Error("artifact_materialization_not_found", "The materialization was not found for this Artifact.");
            materialized = await materializations.GetAsync(artifactId, materialized.FlowRunId, token)
                ?? throw Error("artifact_materialization_not_found", "The materialization was not found for this Artifact.");
            while (materialized.Status is "Pending" or "Running" or "WaitingForChild")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, token);
                materialized = await materializations.GetAsync(artifactId, materialized.FlowRunId, token)
                    ?? throw Error("artifact_materialization_not_found", "The materialization was not found for this Artifact.");
            }
            if (materialized.Status != "Succeeded" || !materialized.StagedArtifactAvailable
                || materialized.StagedArtifactId is not { } stagedId)
                throw Error("artifact_materialization_unavailable", materialized.ErrorMessage ?? "The Artifact materialization is unavailable or requires input.");
            var chunk = await artifacts.ReadAsync(stagedId, offset, length, null, token);
            var bytes = Convert.FromBase64String(chunk.ContentBase64);
            var consumed = bytes.Length;
            string text;
            // Keep an incomplete trailing UTF-8 scalar for the next byte-range request.
            while (true)
            {
                try { text = new UTF8Encoding(false, true).GetString(bytes, 0, consumed); break; }
                catch (DecoderFallbackException) when (!chunk.EndOfContent && consumed > 0 && bytes.Length - consumed < 3)
                { consumed--; }
                catch (DecoderFallbackException)
                { throw Error("durable_artifact_not_utf8", "The selected content is not valid UTF-8 text. Use a binary download instead."); }
            }
            if (consumed == 0 && bytes.Length > 0)
                throw Error("durable_artifact_not_utf8", "The selected offset does not start at a UTF-8 character boundary.");
            return new(artifactId.ToString(), materialized.FlowRunId, artifact.Value.Receipt.MediaType,
                artifact.Value.Receipt.Length, offset, offset + consumed, chunk.EndOfContent && consumed == bytes.Length, text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw Error("artifact_materialization_timed_out", "Artifact materialization exceeded its execution timeout."); }
    }

    private static ArtifactValidationException Error(string code, string message) => new(code, message);
}
