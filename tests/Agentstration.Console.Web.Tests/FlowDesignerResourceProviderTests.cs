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

        var agents = await provider.GetAgentsAsync(CancellationToken.None);

        var agent = agents.Single(item => item.Name == "dotnet-expert");
        Assert.AreEqual(".NET Expert", agent.DisplayName);
    }
}
