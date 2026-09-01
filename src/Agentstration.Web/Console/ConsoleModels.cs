using Agentstration.Flow;
using Agentstration.Resources;
using Agentstration.Web.Components.Models;
using Agentstration.Work;

namespace Agentstration.Web.Console;

public sealed record AgentSummary(string Id, string Name, string Type, string Version, string Status, IReadOnlyList<string> Capabilities, string Runtime, DateTimeOffset LastActivity, string ModelProfile = "Not configured")
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public ResourceNamespace ModelProfileNamespace { get; init; } = ResourceNamespace.Default;
    public string? DeploymentId { get; init; }
    public ResourceAddress ModelProfileAddress => ResourceAddress.Create(ModelProfileNamespace, Agentstration.Management.Abstractions.ResourceKinds.ModelProfile, ModelProfile);
    public string? DeploymentUrl => DeploymentId is null ? null : ConsoleResourceUrls.Deployment(Namespace, DeploymentId);
    public string DetailsUrl => Namespace.IsDefault
        ? $"/agents/{Uri.EscapeDataString(Id)}"
        : $"/namespaces/{Uri.EscapeDataString(Namespace.Value)}/agents/{Uri.EscapeDataString(Id)}";
}
public sealed record ResourceSnapshot<T>(T Value, string ETag);
public sealed record DeploymentSummary(
    string Id,
    string Agent,
    string Namespace,
    string Status,
    string DesiredState,
    string HostingMode,
    string Environment,
    string RuntimeProfile,
    string Revision,
    string? ObservedRevision,
    DateTimeOffset UpdatedAt,
    string? Error = null);
public sealed record WorkSummary(Guid Id, string Title, string Type, string Status, string Priority, string Owner, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record FlowSummary(string Id, string Name, string Kind, string Version, string Status, int Steps, int ActiveExecutions, DateTimeOffset UpdatedAt)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public string DetailsUrl => ConsoleResourceUrls.Flow(new FlowId(Id, Namespace));
}

public static class ConsoleResourceUrls
{
    public static string Deployment(ResourceNamespace @namespace, string name) =>
        $"/deployments#deployment-{Uri.EscapeDataString(@namespace.Value)}-{Uri.EscapeDataString(name)}";

    public static string Flow(FlowId id) => id.Namespace.IsDefault
        ? $"/flows/{Uri.EscapeDataString(id.Value)}"
        : $"/namespaces/{Uri.EscapeDataString(id.Namespace.Value)}/flows/{Uri.EscapeDataString(id.Value)}";

    public static string Entry(EntryId id) => id.Namespace.IsDefault
        ? $"/entries/{Uri.EscapeDataString(id.Value)}"
        : $"/namespaces/{Uri.EscapeDataString(id.Namespace.Value)}/entries/{Uri.EscapeDataString(id.Value)}";

    public static string ModelProfile(ResourceAddress address)
    {
        var path = $"/modelprofiles/{Uri.EscapeDataString(address.Name)}";
        return address.Namespace.IsDefault
            ? path
            : $"{path}?namespace={Uri.EscapeDataString(address.Namespace.Value)}";
    }

    public static string RuntimeProfile(ResourceAddress address)
    {
        var path = $"/runtimeprofiles/{Uri.EscapeDataString(address.Name)}";
        return $"{path}?namespace={Uri.EscapeDataString(address.Namespace.Value)}";
    }
}
public sealed record ExecutionSummary(string Id, string Agent, string? Flow, Guid? WorkItemId, string Status, DateTimeOffset StartedAt, TimeSpan? Duration, string? Result, string? Error);
public sealed record ManagementSummary(int Agents, int Configurations, int Revisions, int Policies, string DesiredState);
public sealed record PlatformSnapshot
{
    public required string Status { get; init; }
    public int DefinedAgents { get; init; }
    public int ReadyDeployments { get; init; }
    public int DesiredDeployments { get; init; }
    public int RunningRuntimeRuns { get; init; }
    public int RunningTasks { get; init; }
    public int ActionRequiredTasks { get; init; }
    public int FailedTasks { get; init; }
    public int CompletedTasksLast24Hours { get; init; }
    public int EnabledFlows { get; init; }
    public int RunningFlowRuns { get; init; }
    public int WaitingForInputFlowRuns { get; init; }
    public int EnabledTriggers { get; init; }
    public int FailedTriggers { get; init; }
    public int ReadyModelProviders { get; init; }
    public int UnavailableModelProviders { get; init; }
    public int AttentionCount { get; init; }
    public IReadOnlyList<ComponentHealth> AttentionItems { get; init; } = [];
    public IReadOnlyList<ComponentHealth> Sources { get; init; } = [];
}
public sealed record DashboardMetric(string Value, string? Detail, UiStatus Status);
public sealed record PlatformDashboardLoad(
    Task<DashboardMetric> DefinedAgents,
    Task<DashboardMetric> ReadyDeployments,
    Task<DashboardMetric> RuntimeRuns,
    Task<DashboardMetric> RunningTasks,
    Task<DashboardMetric> NeedsAttention,
    Task<DashboardMetric> FlowRuns,
    Task<DashboardMetric> EnabledTriggers,
    Task<DashboardMetric> ModelProviders,
    Task<PlatformSnapshot> Snapshot);
public sealed record ComponentHealth(string Name, string Status, string Detail, UiStatus Severity, string? Url = null);
