using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;

namespace Agentstration.Web.Tests;

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
