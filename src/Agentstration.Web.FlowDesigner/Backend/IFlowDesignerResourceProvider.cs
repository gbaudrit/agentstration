using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Resources;

namespace Agentstration.Web.FlowDesigner.Backend;

public sealed record FlowDesignerAgent(string Name, string DisplayName, ResourceScopeRef ScopeRef)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record FlowDesignerFlow(string Name, string DisplayName, ResourceNamespace Namespace, ResourceScopeRef ScopeRef, string? ActiveVersion);
public sealed record FlowDesignerOutput(string Name, string? DisplayName, FlowOutputOutcome Outcome, JsonElement? Schema);
public sealed record FlowDesignerFlowVersion(string Version, JsonElement? InputSchema, JsonElement? OutputSchema, IReadOnlyList<FlowDesignerOutput>? Outputs = null)
{
    public IReadOnlyList<FlowDesignerOutput> NamedOutputs { get; init; } = Outputs ?? [];
}
public sealed record FlowDesignerTool(
    string Name,
    string DisplayName,
    ResourceNamespace Namespace,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    bool Enabled,
    bool Available,
    bool RequiresApproval,
    ResourceScopeRef ScopeRef);

public interface IFlowDesignerResourceProvider
{
    Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowDesignerFlow>> GetFlowsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FlowDesignerFlow>>([]);
    Task<IReadOnlyList<FlowDesignerFlowVersion>> GetFlowVersionsAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FlowDesignerFlowVersion>>([]);
    Task<IReadOnlyList<FlowDesignerTool>> GetToolsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FlowDesignerTool>>([]);
}
