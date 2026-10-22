using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentRunnerModelTests
{
    [TestMethod]
    public void InteractiveRequestKeepsDefinitionScopeWithoutSelectingExecutionScope()
    {
        var agent = new AgentResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = "instance-agent" },
            ScopeRef = ResourceScopeRef.Instance,
            Generation = 3,
            Definition = new AgentProperties
            {
                DisplayName = "Instance agent",
                Instructions = "Respond deterministically.",
                ModelProfile = new ResourceReference("reasoning", ResourceScopeRef.Instance),
            },
        };
        var model = new AgentRunnerModel { Prompt = "Run from the selected Workspace." };

        var request = model.ToRequest(agent);

        Assert.IsNull(request.ExecutionScopeRef);
        Assert.AreEqual(ResourceScopeRef.Instance, request.Agent.ScopeRef);
    }
}
