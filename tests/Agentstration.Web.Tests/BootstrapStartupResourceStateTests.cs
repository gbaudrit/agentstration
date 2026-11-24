using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Infrastructure;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupResourceStateTests
{
    [TestMethod]
    public void LaterContributionWaitsKeepTheirReasonsWithoutReplacingTheFirstBlockingResource()
    {
        var state = new BootstrapStartupState(); state.Begin(); state.MoveTo("extensions");
        var first = state.RegisterResource("/instance", "nantes", "postgres.yaml", "extensions", "ExtensionContribution", "postgres");
        var later = state.RegisterResource("/instance", "nantes", "openapi.yaml", "extensions", "ExtensionContribution", "openapi");
        state.ObserveContributionWait("ConnectedService", "database", "postgres", "postgresql", first, "extension_unavailable");
        state.ObserveOperation(first, "contribution_availability", "waiting");
        state.ObserveContributionWait("ConnectedService", "alerts", "openapi", "openapi", later, makeCurrent: false);
        state.ObserveOperation(later, "contribution_availability", "waiting");
        var snapshot = state.Read();
        Assert.AreEqual(first, snapshot.Activity?.ResourceId);
        var rows = state.ReadResources(new(snapshot.AttemptId))!.Items;
        Assert.AreEqual("extension_unavailable", rows[0].WaitReason);
        Assert.AreEqual("contribution_unavailable", rows[1].WaitReason);
        state.ObserveContributionWait("ConnectedService", "database", "postgres", "postgresql", first, "extension_unavailable");
        state.ObserveContributionWait("ConnectedService", "alerts", "openapi", "openapi", later, makeCurrent: false);
        Assert.AreEqual(snapshot.Revision, state.Read().Revision, "Polling unchanged dependencies must not rotate the active resource.");
    }

    [TestMethod]
    public void ContributionWaitIdentifiesTheExactDependencyAndActiveFamilyWithoutLosingOtherWaits()
    {
        var state = new BootstrapStartupState(); state.Begin(); state.MoveTo("extensions");
        var first = state.RegisterResource("/instance", "nantes", "contribution:postgresql", "extensions", "ExtensionContribution", "postgresql",
            dependency: new("extension.postgresql", "ConnectedServiceType", "postgresql", "1.0.0", null, "ConnectedService", "database"));
        var second = state.RegisterResource("/instance", "nantes", "contribution:jsonb", "extensions", "ExtensionContribution", "jsonb",
            dependency: new("extension.postgresql", "StorageType", "postgresql.jsonb", "2.0.0", null, "Storage", "documents"));
        state.ObserveContributionWait("ConnectedService", "database", "extension.postgresql", "postgresql", first, "extension_unavailable");
        state.ObserveOperation(first, "contribution_availability", "waiting");
        state.ObserveContributionWait("Storage", "documents", "extension.postgresql", "postgresql.jsonb", second);
        state.ObserveOperation(second, "contribution_availability", "waiting");
        var snapshot = state.Read();
        var page = state.ReadResources(new(snapshot.AttemptId))!;
        Assert.AreEqual(second, snapshot.Activity?.ResourceId);
        Assert.AreEqual(page.Items[1].GroupId, snapshot.Activity?.GroupId);
        Assert.AreEqual("/instance", snapshot.Activity?.TargetLevel);
        Assert.AreEqual("extension_unavailable", page.Items[0].WaitReason);
        Assert.AreEqual("StorageType", page.Items[1].Dependency?.ContributionKind);
        Assert.AreEqual("2.0.0", page.Items[1].Dependency?.Version);
        state.FinishResource(first, "succeeded");
        Assert.IsNull(state.ReadResources(new(snapshot.AttemptId))!.Items[0].WaitReason);
        Assert.AreEqual("extension_unavailable", page.Items[0].WaitReason, "Already returned pages remain immutable.");
    }

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
    public void InterleavedPoliciesKeepTheirExecutionPositionAndTheirOwnProgress()
    {
        var state = new BootstrapStartupState(); state.Begin(); state.MoveTo("resources");
        var kinds = new[] { "ModelProvider", "ResourceAccessPolicy", "ModelProfile", "ResourceAccessPolicy", "Agent" };
        for (var index = 0; index < kinds.Length; index++)
            state.RegisterResource("/instance", "resources", index + ".yaml", "resources", kinds[index], "item-" + index);
        var planned = state.Read();
        CollectionAssert.AreEqual(kinds, planned.Families.Select(family => family.Kind).ToArray());
        var policies = planned.Families.Where(family => family.Kind == "ResourceAccessPolicy").ToArray();
        Assert.AreNotEqual(policies[0].GroupId, policies[1].GroupId);
        state.StartResource("/instance", "resources", "3.yaml", kinds[3], "item-3", "default");
        state.ObserveResource(kinds[3], "item-3");
        Assert.AreEqual(policies[1].GroupId, state.Read().Activity?.GroupId);
        var page = state.ReadResources(new(planned.AttemptId, GroupId: policies[1].GroupId))!;
        Assert.AreEqual(1, page.Total);
        Assert.AreEqual("item-3", page.Items.Single().Name);
        Assert.AreEqual("running", state.Read().Families[3].State);
        Assert.AreEqual("pending", state.Read().Families[1].State);
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
