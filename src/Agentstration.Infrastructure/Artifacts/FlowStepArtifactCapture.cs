using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;

namespace Agentstration.Infrastructure.Artifacts;

public sealed class FlowStepArtifactCapture(ArtifactManagementService artifacts) : IFlowStepArtifactCapture
{
    public async Task<FlowStepArtifactReference> CaptureAsync(
        FlowStepArtifactCaptureRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CaptureCoreAsync(request, cancellationToken);
        }
        catch (ArtifactValidationException exception)
        {
            throw new FlowValidationException(exception.Code, exception.Message);
        }
        catch (AuthorizationDeniedException exception)
        {
            throw new FlowValidationException("flow_step_artifact_access_denied", exception.Message);
        }
    }

    private async Task<FlowStepArtifactReference> CaptureCoreAsync(
        FlowStepArtifactCaptureRequest request,
        CancellationToken cancellationToken)
    {
        var operationId = $"flow-step:{request.FlowRunId}:{request.StepName}:{request.Attempt}";
        var fileName = request.Definition.FileName ?? DefaultFileName(request.StepName, request.Definition.MediaType);
        var content = Serialize(request.Content, request.Definition.MediaType, request.Definition.ContentEncoding);
        var expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        if (request.Definition.MaximumBytes is { } maximumBytes && content.LongLength > maximumBytes)
            throw Error("flow_step_artifact_too_large",
                $"The selected step result exceeds the configured maximum of {maximumBytes} bytes.");
        var existing = await artifacts.GetStagedByProducerIdForWriteAsync(operationId, cancellationToken);
        var staged = existing ?? await artifacts.CreateStagedAsync(new(
            fileName,
            request.Definition.MediaType,
            new ArtifactProducer
            {
                Kind = ArtifactProducerKind.FlowRun,
                Id = operationId,
                FlowRunId = request.FlowRunId,
                FlowStepId = request.StepName,
                CorrelationId = request.CorrelationId,
                Provenance = request.Provenance
            }, request.Definition.StagingBinding), cancellationToken);

        if (!string.Equals(staged.Value.FileName, fileName, StringComparison.Ordinal)
            || !string.Equals(staged.Value.MediaType, request.Definition.MediaType, StringComparison.OrdinalIgnoreCase))
            throw Error("flow_step_artifact_capture_conflict",
                "The existing step Artifact does not match the current capture definition.");

        if (staged.Value.ArtifactStatus == StagedArtifactStatus.Open)
        {
            if (staged.Value.Length > content.Length)
                throw Error("flow_step_artifact_capture_conflict",
                    "The existing step Artifact is longer than the result being captured.");
            var offset = staged.Value.Length;
            while (offset < content.Length)
            {
                var length = Math.Min(ArtifactManagementService.MaximumChunkBytes,
                    content.Length - checked((int)offset));
                staged = await artifacts.WriteAsync(staged.Value.ArtifactId, offset,
                    content.AsMemory(checked((int)offset), length), cancellationToken);
                offset = staged.Value.Length;
            }
            staged = await artifacts.SealAsync(staged.Value.ArtifactId, cancellationToken);
        }
        else if (staged.Value.ArtifactStatus is not (StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted))
        {
            throw Error("flow_step_artifact_capture_unavailable",
                $"The existing step Artifact cannot be reused in state '{staged.Value.ArtifactStatus}'.");
        }
        if (!string.Equals(staged.Value.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw Error("flow_step_artifact_capture_conflict",
                "The existing step Artifact content does not match the result being captured.");

        return new(staged.Value.ArtifactId.ToString(), staged.Value.FileName, staged.Value.MediaType);
    }

    private static byte[] Serialize(
        JsonElement content,
        string mediaType,
        FlowStepArtifactContentEncoding encoding)
    {
        var resolved = encoding == FlowStepArtifactContentEncoding.Auto
            ? IsJson(mediaType) ? FlowStepArtifactContentEncoding.Json
            : content.ValueKind == JsonValueKind.String ? FlowStepArtifactContentEncoding.Utf8
            : throw Error("flow_step_artifact_encoding_ambiguous",
                "A non-JSON structured result requires an explicit contentEncoding.")
            : encoding;
        return resolved switch
        {
            FlowStepArtifactContentEncoding.Json => Encoding.UTF8.GetBytes(content.GetRawText()),
            FlowStepArtifactContentEncoding.Utf8 when content.ValueKind == JsonValueKind.String =>
                Encoding.UTF8.GetBytes(content.GetString() ?? string.Empty),
            FlowStepArtifactContentEncoding.Base64 when content.ValueKind == JsonValueKind.String => DecodeBase64(content.GetString()!),
            FlowStepArtifactContentEncoding.Utf8 => throw Error("flow_step_artifact_utf8_string_required",
                "UTF-8 Artifact capture requires a string result."),
            FlowStepArtifactContentEncoding.Base64 => throw Error("flow_step_artifact_base64_string_required",
                "Base64 Artifact capture requires a string result."),
            _ => throw Error("flow_step_artifact_encoding_invalid", "The Artifact content encoding is invalid.")
        };
    }

    private static byte[] DecodeBase64(string content)
    {
        try { return Convert.FromBase64String(content); }
        catch (FormatException exception)
        {
            throw new ArtifactValidationException("flow_step_artifact_base64_invalid", exception.Message);
        }
    }

    private static bool IsJson(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    private static string DefaultFileName(string stepName, string mediaType) =>
        $"{stepName}{(IsJson(mediaType) ? ".json" : mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? ".txt" : ".bin")}";

    private static ArtifactValidationException Error(string code, string message) => new(code, message);
}
