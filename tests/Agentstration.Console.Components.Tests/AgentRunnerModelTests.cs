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
            Uid = Guid.Parse("97a3927c-1554-4200-9583-7d93cdb24614"),
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
        Assert.AreEqual(agent.Uid, request.Agent.ResourceUid);
    }
}
