using Agentstration.Web.Console;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DashboardTests
{
    [TestMethod]
    public async Task SimulatedDashboardAggregatesEveryPlane()
    {
        var fake = new MockApiClient(new FixedTimeProvider(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)));
        var service = new PlatformDashboardService(fake, fake, fake, fake);

        var snapshot = await service.GetAsync(CancellationToken.None);

        Assert.AreEqual(2, snapshot.KnownAgents);
        Assert.AreEqual(0, snapshot.ActiveAgents);
        Assert.AreEqual(3, snapshot.OpenWorkItems);
        Assert.AreEqual("Degraded", snapshot.Status);
    }

    [TestMethod]
    public async Task SimulatedManagementClientSupportsCrudAndConcurrency()
    {
        var fake = new MockApiClient(new FixedTimeProvider(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)));
        var request = new AgentResourceRequest
        {
            Type = AgentstrationResourceTypes.Agents,
            ApiVersion = ManagementApiVersions.V20260801,
            Name = "new-agent",
            ResourceGroup = "default",
            Location = "local",
            Properties = new AgentProperties
            {
                DisplayName = "New agent",
                AgentType = new AgentTypeReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agentTypes", "readonly-expert").Value, 1),
                ModelProfile = new ResourceReference(ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Models, "modelProfiles", "reasoning-default").Value)
            }
        };

        var created = await fake.PutAgentAsync(request, null, createOnly: true, CancellationToken.None);
        var stale = await Assert.ThrowsAsync<AgentstrationApiException>(() => fake.PutAgentAsync(request, "\"stale\"", createOnly: false, CancellationToken.None));
        await fake.DeleteAgentAsync("default", "new-agent", created.ETag, CancellationToken.None);

        Assert.AreEqual(1L, created.Value.Generation);
        Assert.IsTrue(stale.IsConcurrencyConflict);
        Assert.HasCount(2, await fake.GetAgentsAsync(CancellationToken.None));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
