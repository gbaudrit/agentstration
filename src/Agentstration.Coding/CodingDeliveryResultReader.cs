using System.Text.Json;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;

namespace Agentstration.Coding;

public static class CodingDeliveryResultReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Result<CodingDeliveryExecution> Read(FlowRunScope scope, WorkProjectId project, WorkPlanId plan,
        WorkPlanAttempt attempt, RuntimeWorkerAssignment assignment, CodingExecutionConfiguration configuration)
    {
        if (attempt.Status == WorkPlanAttemptStatus.Completed && assignment.State != RuntimeAssignmentState.Succeeded
            || attempt.Status == WorkPlanAttemptStatus.Cancelled && assignment.State != RuntimeAssignmentState.Cancelled)
            return Failure("coding_delivery_attempt_invalid");
        var usage = CodingUsageAccounting.Read(scope, project, plan, attempt, assignment);
        if (!usage.IsSuccess) return Failure(usage.ErrorCode!);
        CodingTaskInput? input;
        try { input = attempt.Input.Deserialize<CodingTaskInput>(Json); }
        catch (JsonException) { return Failure("coding_delivery_source_invalid"); }
        if (!CodingExecutionValidation.Input(input) || !CodingExecutionValidation.Configuration(configuration, true)) return Failure("coding_delivery_source_invalid");
        CodingNativeResult? retained = null; string? digest = null; long bytes = 0;
        try
        {
            foreach (var item in assignment.ExecutionEvents)
            {
                if (item.RunId != attempt.FlowRunId || item.Payload is not { ValueKind: JsonValueKind.Object } payload) continue;
                JsonElement? candidate = payload.TryGetProperty("codingResult", out var final) ? final
                    : payload.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object && output.TryGetProperty("coordinates", out _) ? output : null;
                if (candidate is null) continue;
                bytes += System.Text.Encoding.UTF8.GetByteCount(candidate.Value.GetRawText());
                if (bytes > 2 * 1024 * 1024) return Failure("coding_delivery_source_limit");
                var result = candidate.Value.Deserialize<CodingNativeResult>(Json);
                if (result?.Coordinates is not { } coordinates) return Failure("coding_delivery_source_invalid");
                if (coordinates.AssignmentAttemptId != assignment.CurrentAttempt?.Id.Value) continue;
                if (coordinates.WorkspaceId != scope.WorkspaceId || coordinates.PrincipalId != scope.PrincipalId || coordinates.ProjectId != project
                    || coordinates.PlanId != plan || coordinates.PlanRevision != attempt.PlanRevision || coordinates.PlanDigest != attempt.PlanDigest
                    || coordinates.TaskId != attempt.TaskId || coordinates.TaskVersion != attempt.TaskVersion || coordinates.TaskDigest != attempt.TaskDigest
                    || coordinates.TaskAttemptId != attempt.Id || coordinates.WorkItemId != attempt.WorkItemId || coordinates.FlowRunId != attempt.FlowRunId
                    || coordinates.AssignmentId != assignment.Id.Value || coordinates.InvocationId != coordinates.AssignmentAttemptId
                    || result.BaseRevision != input!.BaseRevision || result.Checks is not { Count: <= 8 }
                    || result.Consumption is not { Count: <= 32 } || result.Summary is not { Length: <= 8_000 }) return Failure("coding_delivery_source_invalid");
                var currentDigest = WorkPlanDefinition.Digest(result);
                if (digest is not null && digest != currentDigest) return Failure("coding_delivery_result_conflict");
                retained = result; digest = currentDigest;
            }
        }
        catch (JsonException) { return Failure("coding_delivery_source_invalid"); }
        if (retained is null) return Failure("coding_delivery_result_unavailable");
        if (attempt.Status == WorkPlanAttemptStatus.Completed && (retained.Diagnostic is not null || retained.CliExitCode != 0
            || configuration.Checks.Count == 0 || retained.Checks.Count != configuration.Checks.Count
            || retained.Checks.Any(check => check is null || !check.Completed || check.ExitCode != 0 || check.Diagnostic is not null)
            || !configuration.Checks.Select(check => check.Name).Order(StringComparer.Ordinal).SequenceEqual(retained.Checks.Select(check => check.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || !CodingCodeChanges.Valid(retained.CodeChanges))) return Failure(retained.CodeChangesDiagnostic is { Length: > 0 and <= 128 } code
                && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ? code : "coding_delivery_checks_or_code_required");
        return Result<CodingDeliveryExecution>.Success(new(retained, configuration, digest!));
    }
    private static Result<CodingDeliveryExecution> Failure(string code) => Result<CodingDeliveryExecution>.Failure(code);
}
