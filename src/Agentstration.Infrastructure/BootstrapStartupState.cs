using Agentstration.Bootstrap.Contracts;

namespace Agentstration.Infrastructure;

public sealed partial class BootstrapStartupState(TimeProvider? timeProvider = null) : IBootstrapStartupStatus
{
    private readonly object sync = new();
    private TaskCompletionSource? completion;
    private TaskCompletionSource attemptChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Guid attemptId;
    private long revision;
    private string? failure;
    private BootstrapFailure? failureDiagnostic;
    private bool administrationReady;
    private string? activeStep;
    private BootstrapStartupActivity? activity;
    private int created, skipped, conflicts, failed;
    private readonly Dictionary<string, string> steps = new(StringComparer.Ordinal);
    public bool IsReady { get { lock (sync) return completion is null || completion.Task.IsCompletedSuccessfully; } }
    public string? Failure { get { lock (sync) return failure; } }
    public bool AdministrationReady { get { lock (sync) return administrationReady; } }
    public void EnableAdministration() { lock (sync) { administrationReady = true; revision++; } }

    public void Begin()
    {
        lock (sync)
        {
            attemptChanged.TrySetResult();
            attemptChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            attemptId = Guid.NewGuid(); revision = 1; failure = null; failureDiagnostic = null; administrationReady = false;
            created = skipped = conflicts = failed = 0; activity = null;
            resourceRows.Clear(); lastGroupByStep.Clear(); activeResourceId = null;
            steps.Clear();
            foreach (var id in new[] { "topology", "extensions", "resources", "finalization" }) steps.Add(id, "pending");
            activeStep = "topology"; steps[activeStep] = "running";
        }
    }
    public void MoveTo(string step)
    {
        lock (sync)
        {
            if (!steps.ContainsKey(step) || failure is not null) return;
            if (activeStep is not null && activeStep != step) steps[activeStep] = "succeeded";
            activeStep = step; steps[step] = "running"; activity = null; revision++;
        }
    }
    public void BeginRetry()
    {
        lock (sync) { Begin(); administrationReady = true; }
    }
    public void ObserveResource(string kind, string name, string? outcome = null)
    {
        lock (sync)
        {
            if (activeStep != "resources" || failure is not null) return;
            // Keep safe dependency identities when its resource fails (for example a model timeout).
            if (outcome != "failed" || activity?.ResourceKind != kind || activity.ResourceName != name)
            {
                var resourceId = activeResourceId ?? (outcome is not null ? activity?.ResourceId : null);
                activity = new(kind, name, ResourceId: resourceId,
                    GroupId: resourceId is { } id && resourceRows.TryGetValue(id, out var row) ? row.GroupId : null);
            }
            steps[activeStep] = "running";
            switch (outcome)
            {
                case "created": created++; break;
                case "skipped": skipped++; break;
                case "conflict": conflicts++; break;
                case "failed": failed++; break;
            }
            revision++;
        }
    }
    public void ObserveModelWait(string resource, string provider, string model, string reason, string? providerNamespace = null, string? providerScopeRef = null, bool canChangeModel = false)
    {
        lock (sync)
        {
            if (activeStep != "resources" || failure is not null) return;
            var next = new BootstrapStartupActivity("ModelProfile", resource, provider, model, reason, providerNamespace, ProviderScopeRef: providerScopeRef, ResourceId: activeResourceId, CanChangeModel: canChangeModel,
                TargetLevel: activeResourceId is { } id && resourceRows.TryGetValue(id, out var row) ? row.TargetLevel : null,
                GroupId: activeResourceId is { } active && resourceRows.TryGetValue(active, out var resourceRow) ? resourceRow.GroupId : null);
            if (activity == next && steps[activeStep] == "waiting") return;
            activity = next; steps[activeStep] = "waiting"; revision++;
            ObserveOperationCore(activeResourceId, "model_discovery", "waiting");
        }
    }
    public void ObserveContributionWait(string kind, string resource, string extension, string contribution)
    {
        lock (sync)
        {
            if (activeStep != "extensions" || failure is not null) return;
            var next = new BootstrapStartupActivity(kind, resource, WaitReason: "contribution_unavailable", ExtensionId: extension, ContributionId: contribution);
            if (activity == next && steps[activeStep] == "waiting") return;
            activity = next; steps[activeStep] = "waiting"; revision++;
        }
    }
    public void Complete()
    {
        lock (sync)
        {
            if (failure is not null) return;
            foreach (var id in steps.Keys) steps[id] = steps[id] == "pending" ? "skipped" : "succeeded";
            activeStep = null; activity = null; revision++;
            completion?.TrySetResult();
        }
    }
    public void Fail(Exception exception) => Fail(exception.Message, Bootstrap.BootstrapFailureDiagnostics.Describe(exception));

    public void Fail(string reason, BootstrapFailure? diagnostic = null)
    {
        lock (sync)
        {
            if (completion?.Task.IsCompleted == true) return;
            failure = reason;
            failureDiagnostic = diagnostic;
            if (activeResourceId is { } id && resourceRows.TryGetValue(id, out var row)
                && row.State is "running" or "waiting")
                resourceRows[id] = row with { State = "failed" };
            if (activeStep is not null) steps[activeStep] = "failed";
            revision++; completion?.TrySetCanceled();
        }
    }
    public BootstrapStartupSnapshot Read()
    {
        lock (sync)
        {
            var ready = completion is null || completion.Task.IsCompletedSuccessfully;
            return new(attemptId, revision, ready, administrationReady,
                ready ? "ready" : failure is null ? "running" : "failed",
                steps.Select(step => new BootstrapStartupStep(step.Key, step.Value)).ToArray(), activeStep,
                activity, created, skipped, conflicts, failed)
            { Families = ReadFamilies(), Failure = resourceRows.Values.LastOrDefault(row => row.Failure is not null)?.Failure ?? failureDiagnostic };
        }
    }
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        lock (sync) return completion?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }
    public Task WaitForAttemptChangeAsync(Guid previousAttempt, CancellationToken cancellationToken)
    {
        lock (sync) return previousAttempt != attemptId ? Task.CompletedTask : attemptChanged.Task.WaitAsync(cancellationToken);
    }
}
