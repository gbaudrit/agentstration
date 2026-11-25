using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;

namespace Agentstration.Runtime.Worker.Coding;

internal sealed class CodingAssignmentExecutor(CodingSandbox sandbox, TimeProvider clock) : IAwpAssignmentExecutor
{
    public async Task<AwpAssignmentExecutionResult> ExecuteAsync(AwpAssignmentSession session, AwpExecutionMaterial material, CancellationToken cancellationToken)
    {
        if (material is not AwpRootFlowExecutionMaterial { RuntimeConfiguration: { } configuration } flow
            || session.Assignment.Runtime.RuntimeKind != CodingExecutionProtocol.RuntimeCapability
            || session.Assignment.Runtime.CapabilityVersion != CodingExecutionProtocol.CapabilityVersion
            || session.Assignment.Runtime.ExecutionMaterialVersion != CodingExecutionProtocol.MaterialVersion
            || material.SchemaVersion != CodingExecutionProtocol.MaterialVersion) throw Unsupported("coding_material_version_unsupported");
        CodingExecutionMaterial? native;
        try { native = configuration.Deserialize<CodingExecutionMaterial>(CodingSandbox.Json); }
        catch (JsonException) { throw Unsupported("coding_material_invalid"); }
        if (!CodingExecutionValidation.Material(native) || native!.Coordinates.FlowRunId != flow.RunId || native.Coordinates.PrincipalId != flow.PrincipalId
            || native.Coordinates.WorkspaceId.ToString() != session.Assignment.Scope.WorkspaceId
            || native.Coordinates.AssignmentId != session.Assignment.AssignmentId.Value || native.Coordinates.AssignmentAttemptId != session.Assignment.AttemptId.Value)
            throw Unsupported("coding_material_invalid");
        var observations = new List<CodingConsumptionObservation>(); CodingNativeResult? result = null;
        var resultId = "coding-result:" + native.Coordinates.InvocationId.ToString("N");
        var location = new AwpExecutionLocation(flow.RunId);
        string? diagnostic = null; var cancelled = false;
        try
        {
            await session.EnsureCanStartMutationAsync(cancellationToken);
            var process = await sandbox.ExecuteAsync(native, async (line, token) =>
            {
                using var document = JsonDocument.Parse(line, new() { MaxDepth = 64 });
                var envelope = document.RootElement;
                if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                    || !envelope.TryGetProperty("value", out var value)) throw new CodingProcessException("coding_helper_event_invalid");
                switch (kind.GetString())
                {
                    case "consumption":
                        var observation = value.Deserialize<CodingConsumptionObservation>(CodingSandbox.Json);
                        if (observation is null || observation.Coordinates != native.Coordinates || observation.ObservationId is not { Length: > 0 and <= 128 }
                            || observations.Count >= 32 || observations.Any(item => item.ObservationId == observation.ObservationId))
                            throw new CodingProcessException("coding_helper_observation_invalid");
                        observations.Add(observation);
                        await session.AppendEventAsync(AwpExecutionEventKind.Diagnostic, location,
                            JsonSerializer.SerializeToElement(new { codingConsumption = observation }, CodingSandbox.Json), null, token);
                        break;
                    case "result":
                        if (result is not null) throw new CodingProcessException("coding_helper_result_duplicate");
                        result = value.Deserialize<CodingNativeResult>(CodingSandbox.Json);
                        if (result is null || result.Coordinates != native.Coordinates || result.BaseRevision != native.Input.BaseRevision
                            || result.Patch is { } patch && (Encoding.UTF8.GetByteCount(patch) > CodingExecutionProtocol.MaximumPatchBytes
                                || result.PatchDigest != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(patch))))
                            || result.Summary is not { Length: <= 8 * 1024 } || result.Checks is not { Count: <= 8 } || result.Consumption is not { Count: <= 32 })
                            throw new CodingProcessException("coding_helper_result_invalid");
                        if (result.Diagnostic is null && (result.CliExitCode != 0 || result.Patch is null
                            || native.Configuration.Checks.Count > 0 && (result.Checks.Count != native.Configuration.Checks.Count
                                || result.Checks.Where((check, index) => check.Name != native.Configuration.Checks[index].Name || !check.Completed || check.ExitCode != 0).Any())
                            || native.Configuration.Checks.Count == 0 && !result.Checks.Any(check => !check.Completed && check.Diagnostic == "checks_not_configured")))
                            throw new CodingProcessException("coding_helper_success_unproven");
                        break;
                    default: throw new CodingProcessException("coding_helper_event_invalid");
                }
            }, cancellationToken);
            if (process.ExitCode != 0 || result is null) diagnostic = "coding_sandbox_execution_failed";
            else diagnostic = result.Diagnostic;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { cancelled = true; diagnostic = "coding_execution_cancelled"; }
        catch (CodingProcessException exception) { diagnostic = exception.Code; }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { diagnostic = "coding_helper_unavailable"; }
        if (observations.Count == 0) observations.Add(new(native.Coordinates.InvocationId.ToString("N") + ":unavailable", native.Coordinates,
            CodingConsumptionAvailability.Unavailable, null, null, null, null, clock.GetUtcNow(), "usage_unreported"));
        result ??= new(native.Coordinates, native.Input.BaseRevision, null, null, "", null, [], observations, diagnostic);
        result = result with { Consumption = observations.ToArray(), Diagnostic = diagnostic };
        // A final receipt is best-effort only under the current lease; it never extends ownership.
        using var receiptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        if (session.CanStartMutation()) await session.AppendEventAsync(AwpExecutionEventKind.Diagnostic, location,
            JsonSerializer.SerializeToElement(new { codingResultId = resultId, codingResult = result }, CodingSandbox.Json), null, receiptDeadline.Token);
        var reference = JsonSerializer.Serialize(new { codingResultId = resultId, native.Coordinates.InvocationId, diagnostic }, CodingSandbox.Json);
        if (cancelled) throw new AwpExecutionCancelledException(reference);
        if (diagnostic == "coding_execution_timed_out") throw new AwpExecutionTimedOutException(diagnostic, "The native Coding execution exceeded its timeout.") { Details = reference };
        if (diagnostic is not null) throw Unsupported(diagnostic, reference);
        return new(JsonSerializer.SerializeToElement(result, CodingSandbox.Json));
    }
    private static AwpExecutionNotSupportedException Unsupported(string code, string? details = null) =>
        new(code, "The native Coding execution could not complete safely.") { Details = details };
}
