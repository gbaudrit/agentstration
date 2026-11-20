namespace Agentstration.Bootstrap.Contracts;

public sealed record BootstrapStartupStep(string Id, string State);

public sealed record BootstrapStartupActivity(
    string? ResourceKind = null, string? ResourceName = null,
    string? ProviderName = null, string? ModelName = null, string? WaitReason = null, string? ProviderNamespace = null,
    string? ExtensionId = null, string? ContributionId = null, string? ProviderScopeRef = null);

public sealed record BootstrapStartupSnapshot(
    Guid AttemptId, long Revision, bool Ready, bool AdministrationReady, string State,
    IReadOnlyList<BootstrapStartupStep> Steps, string? ActiveStepId,
    BootstrapStartupActivity? Activity, int Created, int Skipped, int Conflicts, int Failed);

public sealed record BootstrapStartupAdmission(bool Ready);

/// <summary>Read-only, process-local observation of the current startup attempt.</summary>
public interface IBootstrapStartupStatus
{
    BootstrapStartupSnapshot Read();
}
