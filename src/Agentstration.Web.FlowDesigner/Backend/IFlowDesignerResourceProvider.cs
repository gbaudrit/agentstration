namespace Agentstration.Web.FlowDesigner.Backend;

public sealed record FlowDesignerAgent(string Name, string DisplayName);

public interface IFlowDesignerResourceProvider
{
    Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(CancellationToken cancellationToken);
}
