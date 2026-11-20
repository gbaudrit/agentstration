using Agentstration.Bootstrap.Contracts;

namespace Agentstration.Infrastructure;

public sealed class BootstrapStartupState : IBootstrapStartupStatus
{
    private readonly object sync = new();
    private TaskCompletionSource? completion;
    private Guid attemptId;
    private long revision;
    private string? failure;
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
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            attemptId = Guid.NewGuid(); revision = 1; failure = null; administrationReady = false;
            created = skipped = conflicts = failed = 0; activity = null;
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
    public void ObserveResource(string kind, string name, string? outcome = null)
    {
        lock (sync)
        {
            if (activeStep != "resources" || failure is not null) return;
            // Keep safe dependency identities when its resource fails (for example a model timeout).
            if (outcome != "failed" || activity?.ResourceKind != kind || activity.ResourceName != name)
                activity = new(kind, name);
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
    public void ObserveModelWait(string resource, string provider, string model, string reason, string? providerNamespace = null, string? providerScopeRef = null)
    {
        lock (sync)
        {
            if (activeStep != "resources" || failure is not null) return;
            var next = new BootstrapStartupActivity("ModelProfile", resource, provider, model, reason, providerNamespace, ProviderScopeRef: providerScopeRef);
            if (activity == next && steps[activeStep] == "waiting") return;
            activity = next; steps[activeStep] = "waiting"; revision++;
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
    public void Fail(string reason)
    {
        lock (sync)
        {
            if (completion?.Task.IsCompleted == true) return;
            failure = reason;
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
                activity, created, skipped, conflicts, failed);
        }
    }
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        lock (sync) return completion?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }
}
