namespace Agentstration.Work;

public sealed partial class WorkItem
{
    public void AdmitPlanAttempt(WorkPlanId planId, Guid attemptId, WorkExecutionId executionId, DateTimeOffset now)
    {
        if (TaskHierarchy?.Kind != WorkTaskKind.Atomic || TaskAcceptance is { Outcome: not WorkTaskOutcome.Pending })
            throw new WorkTransitionException("work_plan_task_not_dispatchable", "Only a pending atomic functional task can execute.");
        RequireStatus(WorkItemStatus.Pending, "work_plan_task_not_dispatchable");
        CurrentExecutionId = executionId;
        Touch("WorkPlanAttemptAdmitted", attemptId, WorkInteractionOrigin.System, now);
        _history[^1] = _history[^1] with { Metadata = new Dictionary<string, string> { ["planId"] = planId.ToString(), ["attemptId"] = attemptId.ToString("D") } };
        Status = WorkItemStatus.Queued;
    }

    public void ObservePlanAttempt(Guid eventId, WorkExecutionId executionId, WorkItemStatus status,
        WorkResult? result, WorkError? error, DateTimeOffset now)
    {
        if (CurrentExecutionId != executionId || IsTerminal(Status)) return;
        if (status is not (WorkItemStatus.Completed or WorkItemStatus.Failed or WorkItemStatus.Cancelled)) return;
        Result = result;
        Error = error;
        Transition(status, "WorkPlanAttemptFinished", eventId, WorkInteractionOrigin.Runtime, now);
    }

    public void RetryPlanTask(Guid eventId, DateTimeOffset now)
    {
        if (TaskHierarchy?.Kind != WorkTaskKind.Atomic || TaskAcceptance is { Outcome: not WorkTaskOutcome.Pending }
            || Status is not (WorkItemStatus.Failed or WorkItemStatus.Cancelled))
            throw new WorkTransitionException("work_plan_retry_not_allowed", "Explicit retry requires failed or cancelled execution and pending functional acceptance.");
        Result = null;
        Error = null;
        CurrentExecutionId = null;
        Transition(WorkItemStatus.Pending, "WorkPlanTaskRetryRequested", eventId, WorkInteractionOrigin.Requester, now);
    }
}
