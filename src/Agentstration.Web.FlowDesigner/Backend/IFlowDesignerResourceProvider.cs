using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Web.FlowDesigner.Backend;

public sealed record FlowDesignerAgent(string Name, string DisplayName);
public sealed record FlowDesignerFlow(string Name, string DisplayName, ResourceNamespace Namespace, string? ActiveVersion);
public sealed record FlowDesignerFlowVersion(string Version, JsonElement? InputSchema, JsonElement? OutputSchema);

public interface IFlowDesignerResourceProvider
{
    Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FlowDesignerFlow>> GetFlowsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FlowDesignerFlow>>([]);
    Task<IReadOnlyList<FlowDesignerFlowVersion>> GetFlowVersionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FlowDesignerFlowVersion>>([]);
}
