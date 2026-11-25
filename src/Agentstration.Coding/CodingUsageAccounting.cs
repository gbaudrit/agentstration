using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;

namespace Agentstration.Coding;

/// <summary>Maps persisted native receipts to canonical accounting without retaining execution content.</summary>
public static class CodingUsageAccounting
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Result<IReadOnlyList<WorkUsageExecution>> Read(FlowRunScope scope, WorkProjectId projectId,
        WorkPlanId planId, WorkPlanAttempt attempt, RuntimeWorkerAssignment assignment)
    {
        if (assignment.TargetKind != RuntimeAssignmentTargetKind.FlowRun || assignment.RuntimeCapability != CodingExecutionProtocol.RuntimeCapability
            || assignment.RuntimeCapabilityVersion != CodingExecutionProtocol.CapabilityVersion || assignment.ExecutionScopeRef != scope.ScopeRef || assignment.WorkspaceId != scope.WorkspaceId || assignment.TargetRunId != attempt.FlowRunId
            || assignment.State is RuntimeAssignmentState.Pending or RuntimeAssignmentState.Assigned) return Failure("usage_execution_not_final");
        if (!assignment.TransientPayloadsAvailable) return Failure("usage_source_expired");
        var observations = new Dictionary<string, CodingConsumptionObservation>(StringComparer.Ordinal);
        var events = new Dictionary<Guid, HashSet<Guid>>();
        long bytes = 0;
        try
        {
            foreach (var item in assignment.ExecutionEvents)
            {
                if (item.RunId != attempt.FlowRunId || item.Payload is not { ValueKind: JsonValueKind.Object } payload) continue;
                // Existing Runtime storage owns accepted events. Bound the additional decoding work and retained provenance.
                bytes += System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText());
                if (bytes > 4 * 1024 * 1024) return Failure("usage_source_limit");
                IEnumerable<CodingConsumptionObservation> reports = [];
                if (payload.TryGetProperty("codingConsumption", out var report))
                    reports = [report.Deserialize<CodingConsumptionObservation>(Json) ?? throw new JsonException()];
                if (payload.TryGetProperty("codingResult", out var result))
                {
                    var native = result.Deserialize<CodingNativeResult>(Json) ?? throw new JsonException();
                    if (native.Consumption is not { Count: <= 32 }) return Failure("usage_source_invalid");
                    reports = reports.Concat(native.Consumption);
                }
                if (payload.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object
                    && output.TryGetProperty("coordinates", out _) && output.TryGetProperty("consumption", out var consumption))
                {
                    var completed = consumption.Deserialize<CodingConsumptionObservation[]>(Json) ?? throw new JsonException();
                    if (completed.Length > 32) return Failure("usage_source_limit");
                    reports = reports.Concat(completed);
                }
                foreach (var observation in reports)
                {
                    if (observation is null || !Matches(observation.Coordinates) || !Enum.IsDefined(observation.Availability)
                        || !WorkUsageFormat.ValidObservation(new(observation.ObservationId, observation.InputTokens, observation.OutputTokens,
                            observation.ProviderTotalTokens, observation.CachedInputTokens, observation.ReasoningOutputTokens, observation.ObservedAt, observation.Reason))) return Failure("usage_source_invalid");
                    if (observations.TryGetValue(observation.ObservationId, out var prior) && prior != observation) return Failure("usage_observation_conflict");
                    observations[observation.ObservationId] = observation;
                    if (observations.Count > 1024) return Failure("usage_source_limit");
                    var invocation = observation.Coordinates.InvocationId;
                    if (!events.TryGetValue(invocation, out var ids)) events[invocation] = ids = [];
                    ids.Add(item.EventId);
                    if (ids.Count > 64) return Failure("usage_source_limit");
                }
            }
        }
        catch (JsonException) { return Failure("usage_source_invalid"); }
        if (observations.Count == 0)
        {
            var known = assignment.Attempts.Count == 0;
            var total = FlowRunTokenUsageAggregation.Combine([]);
            var source = new WorkUsageSource(known ? WorkUsageSourceKind.NotStarted : WorkUsageSourceKind.Unavailable, "coding-cli/1.0",
                attempt.Id, attempt.WorkItemId.Value, attempt.FlowRunId, attempt.Status)
            { AssignmentId = assignment.Id.Value, InvocationCountKnown = known };
            return Result<IReadOnlyList<WorkUsageExecution>>.Success([new(source, new(total, total), [])]);
        }
        var captures = new List<WorkUsageExecution>();
        foreach (var group in observations.Values.GroupBy(value => value.Coordinates.InvocationId).OrderBy(value => value.Key))
        {
            var reports = group.OrderBy(value => value.ObservationId, StringComparer.Ordinal).ToArray();
            if (reports.Length > 32 || reports.Select(value => value.Coordinates).Distinct().Count() != 1) return Failure("usage_source_invalid");
            var coordinates = reports[0].Coordinates;
            var total = FlowRunTokenUsageAggregation.MergeInvocationReports(reports.Select(value =>
                FlowRunTokenUsageAggregation.Invocation(value.InputTokens, value.OutputTokens, value.ProviderTotalTokens)).ToArray());
            var source = new WorkUsageSource(WorkUsageSourceKind.CodingCli, "coding-cli/1.0", attempt.Id, attempt.WorkItemId.Value, attempt.FlowRunId, attempt.Status)
            {
                AssignmentId = assignment.Id.Value,
                AssignmentAttemptId = coordinates.AssignmentAttemptId,
                InvocationId = coordinates.InvocationId,
                EventIds = events[group.Key].Order().ToArray()
            };
            var totals = new WorkUsageTotals(total, total)
            {
                CachedInputTokens = Sum(reports.Select(value => value.CachedInputTokens)),
                ReasoningOutputTokens = Sum(reports.Select(value => value.ReasoningOutputTokens))
            };
            captures.Add(new(source, totals, reports.Select(value => new WorkUsageObservation(value.ObservationId, value.InputTokens, value.OutputTokens,
                value.ProviderTotalTokens, value.CachedInputTokens, value.ReasoningOutputTokens, value.ObservedAt, value.Reason)).ToArray()));
        }
        foreach (var unreported in assignment.Attempts.Where(value => !observations.Values.Any(observation => observation.Coordinates.AssignmentAttemptId == value.Id.Value)))
        {
            var total = FlowRunTokenUsageAggregation.Combine([]);
            var source = new WorkUsageSource(WorkUsageSourceKind.Unavailable, "coding-cli/1.0", attempt.Id, attempt.WorkItemId.Value, attempt.FlowRunId, attempt.Status)
            { AssignmentId = assignment.Id.Value, AssignmentAttemptId = unreported.Id.Value, InvocationCountKnown = false };
            captures.Add(new(source, new(total, total), []));
        }
        return captures.Count > 32 ? Failure("usage_source_limit") : Result<IReadOnlyList<WorkUsageExecution>>.Success(captures);

        bool Matches(CodingInvocationCoordinates value) => value is not null && value.WorkspaceId == scope.WorkspaceId && value.PrincipalId == scope.PrincipalId
            && value.ProjectId == projectId && value.PlanId == planId && value.PlanRevision == attempt.PlanRevision && value.PlanDigest == attempt.PlanDigest
            && value.TaskId == attempt.TaskId && value.TaskVersion == attempt.TaskVersion && value.TaskDigest == attempt.TaskDigest && value.TaskAttemptId == attempt.Id
            && value.WorkItemId == attempt.WorkItemId && value.FlowRunId == attempt.FlowRunId && value.AssignmentId == assignment.Id.Value
            && value.InvocationId != Guid.Empty && assignment.Attempts.Any(item => item.Id.Value == value.AssignmentAttemptId);
    }
    private static long? Sum(IEnumerable<long?> values)
    {
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return known.Length == 0 ? null : known.Sum();
    }
    private static Result<IReadOnlyList<WorkUsageExecution>> Failure(string code) => Result<IReadOnlyList<WorkUsageExecution>>.Failure(code);
}
