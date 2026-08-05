namespace Agentstration.Web.FlowDesigner.Backend;

public sealed record FlowDesignerAgent(string ResourceId, string DisplayName, string ResourceGroup);

public interface IFlowDesignerResourceProvider
{
    Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(string resourceGroup, CancellationToken cancellationToken);
}
