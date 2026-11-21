using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Infrastructure;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupResourceStateTests
{
    [TestMethod]
    public void OnlySelectedFamiliesAreSummarizedAndResourceIdentityIncludesTargetAndProfile()
    {
        var state = new BootstrapStartupState(); state.Begin();
        var first = state.RegisterResource("/tenants/one", "agents", "agent.yaml", "resources", "Agent", "assistant");
        var second = state.RegisterResource("/tenants/two", "agents", "agent.yaml", "resources", "Agent", "assistant");
        Assert.AreNotEqual(first, second);
        Assert.AreEqual(first, state.RegisterResource("/tenants/one", "agents", "agent.yaml", "resources", "Agent", "assistant"));
        state.RegisterResource("/instance", "topology", "tenant.yaml", "topology", "Tenant", "one");
        var snapshot = state.Read();
        Assert.AreEqual(2, snapshot.Families.Count);
        Assert.AreEqual(2, snapshot.Families.Single(family => family.Kind == "Agent").Total);
        Assert.IsTrue(snapshot.Families.All(family => family.Total > 0));
        Assert.IsFalse(snapshot.Families.Any(family => family.Kind == "Flow"));
    }

    [TestMethod]
    public void LargePlansReturnBoundedPagesAndExactFilteredCountsWithoutDumpingRowsInSnapshots()
    {
        var state = new BootstrapStartupState(); state.Begin();
        for (var index = 0; index < 512; index++)
        {
            var id = state.RegisterResource("/tenants/dev", "load-test", $"{index}.yaml", "resources", "Parameter", $"item-{index:D3}");
            if (index < 500) state.FinishResource(id, "created");
        }
        var snapshot = state.Read();
        var family = snapshot.Families.Single();
        Assert.AreEqual(512, family.Total);
        Assert.AreEqual(500, family.Created);
        var page = state.ReadResources(new(snapshot.AttemptId, "resources", "Parameter", Offset: 500, Limit: 25))!;
        Assert.AreEqual(512, page.Total);
        Assert.AreEqual(12, page.Items.Count);
        Assert.AreEqual("item-500", page.Items[0].Name);
        var filtered = state.ReadResources(new(snapshot.AttemptId, State: "pending", Search: "item-51"))!;
        Assert.AreEqual(2, filtered.Total);
        Assert.AreEqual(2, filtered.Items.Count);
        Assert.IsTrue(JsonSerializer.Serialize(snapshot).Length < 3000);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => state.ReadResources(new(snapshot.AttemptId, Limit: 101)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => state.ReadResources(new(snapshot.AttemptId, Offset: -1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => state.ReadResources(new(snapshot.AttemptId, Search: new string('x', 129))));
        state.Begin();
        Assert.IsNull(state.ReadResources(new(snapshot.AttemptId)));
    }

    [TestMethod]
    public void OperationsAndFailuresRemainScopedImmutableAndSanitized()
    {
        var state = new BootstrapStartupState(); state.Begin(); state.MoveTo("resources");
        var id = state.RegisterResource("/instance", "models", "profile.yaml", "resources", "ModelProfile", "chat", "custom");
        state.RegisterResource("/instance", "agents", "agent.yaml", "resources", "Agent", "assistant");
        state.StartResource("/instance", "models", "profile.yaml", "ModelProfile", "chat", "custom");
        state.ObserveModelWait("chat", "provider", "model:exact", "required_model_missing");
        var waiting = state.ReadResources(new(state.Read().AttemptId))!;
        Assert.AreEqual("waiting", waiting.Items[0].State);
        Assert.AreEqual(id, state.Read().Activity?.ResourceId);
        Assert.AreEqual("/instance", state.Read().Activity?.TargetLevel);
        state.ObserveOperation(id, "model_discovery", "succeeded");
        state.FinishResource(id, "created");
        var revision = state.Read().Revision;
        state.FinishResource(id, "created");
        Assert.AreEqual(revision, state.Read().Revision);
        state.Fail("provider credential must never reach the browser");
        var failed = state.ReadResources(new(state.Read().AttemptId))!;
        Assert.AreEqual("blocked", failed.Items[1].State);
        Assert.AreEqual("waiting", waiting.Items[0].State);
        Assert.AreEqual("waiting", waiting.Items[0].Operations.Single(operation => operation.Code == "model_discovery").State);
        Assert.IsFalse(JsonSerializer.Serialize(failed).Contains("credential", StringComparison.Ordinal));
    }
}
