using Agentstration.Resources;
using Agentstration.Console.Web.Features.Flows;
using Agentstration.Web.Console;

namespace Agentstration.Console.Web.Tests;

[TestClass]
public sealed class FlowDesignerResourceProviderTests
{
    [TestMethod]
    public async Task AgentListKeepsTechnicalIdSeparateFromDisplayName()
    {
        var client = new MockApiClient(TimeProvider.System);
        var provider = new FlowDesignerResourceProvider(client, client, null!);

        var scope = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var agents = await provider.GetAgentsAsync(scope, CancellationToken.None);

        var agent = agents.Single(item => item.Name == "dotnet-expert");
        Assert.AreEqual(".NET Expert", agent.DisplayName);
        Assert.AreEqual(scope, agent.ScopeRef);
    }
}
