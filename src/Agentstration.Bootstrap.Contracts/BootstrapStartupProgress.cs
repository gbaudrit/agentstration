namespace Agentstration.Bootstrap.Contracts;

public sealed record BootstrapStartupStep(string Id, string State);

public sealed record BootstrapStartupActivity(
    string? ResourceKind = null, string? ResourceName = null,
    string? ProviderName = null, string? ModelName = null, string? WaitReason = null, string? ProviderNamespace = null,
    string? ExtensionId = null, string? ContributionId = null, string? ProviderScopeRef = null,
    string? ResourceId = null, bool CanChangeModel = false, string? TargetLevel = null, string? GroupId = null,
    string? FlowRunId = null, string? FlowRunContext = null, DateTimeOffset? StageStartedAt = null,
    string? RuntimeCapability = null, DateTimeOffset? Deadline = null);

public sealed record BootstrapStartupSnapshot(
    Guid AttemptId, long Revision, bool Ready, bool AdministrationReady, string State,
    IReadOnlyList<BootstrapStartupStep> Steps, string? ActiveStepId,
    BootstrapStartupActivity? Activity, int Created, int Skipped, int Conflicts, int Failed)
{
    public BootstrapFailure? Failure { get; init; }
    public IReadOnlyList<BootstrapStartupFamily> Families { get; init; } = [];
}

public sealed record BootstrapStartupAdmission(bool Ready);

/// <summary>Read-only, process-local observation of the current startup attempt.</summary>
public interface IBootstrapStartupStatus
{
    BootstrapStartupSnapshot Read();
    BootstrapStartupResourcePage? ReadResources(BootstrapStartupResourceQuery query)
    {
        var snapshot = Read();
        return query.AttemptId == snapshot.AttemptId ? new(snapshot.AttemptId, snapshot.Revision, 0, query.Offset, query.Limit, []) : null;
    }
}
