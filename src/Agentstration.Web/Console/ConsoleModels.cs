using Agentstration.Resources;
using Agentstration.Web.Components.Models;

namespace Agentstration.Web.Console;

public sealed record AgentSummary(string Id, string Name, string Type, string Version, string Status, IReadOnlyList<string> Capabilities, string Runtime, DateTimeOffset LastActivity, string ModelProfile = "Not configured")
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public string DetailsUrl => Namespace.IsDefault
        ? $"/agents/{Uri.EscapeDataString(Id)}"
        : $"/namespaces/{Uri.EscapeDataString(Namespace.Value)}/agents/{Uri.EscapeDataString(Id)}";
}
public sealed record ResourceSnapshot<T>(T Value, string ETag);
public sealed record RuntimeInstanceSummary(string Id, string Agent, string Status, string HostingMode, string Location, string Activity, double CpuPercent, double MemoryMb, string? Error = null);
public sealed record WorkSummary(Guid Id, string Title, string Type, string Status, string Priority, string Owner, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record FlowSummary(string Id, string Name, string Kind, string Version, string Status, int Steps, int ActiveExecutions, DateTimeOffset UpdatedAt)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public string DetailsUrl => Namespace.IsDefault
        ? $"/flows/{Uri.EscapeDataString(Id)}"
        : $"/namespaces/{Uri.EscapeDataString(Namespace.Value)}/flows/{Uri.EscapeDataString(Id)}";
}
public sealed record ExecutionSummary(string Id, string Agent, string? Flow, Guid? WorkItemId, string Status, DateTimeOffset StartedAt, TimeSpan? Duration, string? Result, string? Error);
public sealed record ManagementSummary(int Agents, int Configurations, int Revisions, int Policies, string DesiredState);
public sealed record PlatformSnapshot(string Status, int KnownAgents, int ActiveAgents, int RunningExecutions, int OpenWorkItems, int ActiveFlows, IReadOnlyList<ComponentHealth> Components);
public sealed record ComponentHealth(string Name, string Status, string Detail, UiStatus Severity);
