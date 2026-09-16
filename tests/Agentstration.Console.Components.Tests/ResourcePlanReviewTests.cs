using System.Text.Json;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ResourcePlanReviewTests
{
    private static readonly ResourcePlanScope Scope = new(Guid.NewGuid(), WorkspaceId.New());
    private static readonly ResourcePlan Plan = new(new(Guid.NewGuid()), Scope, "Support design", "Coordinate support", null,
        new(ResourcePlanningContractVersions.V1, JsonSerializer.SerializeToElement(new
        {
            solution = new { summary = "Assist employees", outcomes = new[] { "Resolve requests" } },
            roles = new[] { new { logicalId = "triage", displayName = "Triage", purpose = "Classify requests", responsibilities = new[] { "Classify" }, capabilities = new[] { "Text analysis" } } }
        })), ResourcePlanStatus.Ready, 2, new(Guid.NewGuid(), FlowRunId: "run-1"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [TestMethod]
    public void ReviewProjectionShowsFieldDifferencesAndDirectedDependencies()
    {
        var role = Change(0, "triage", [], "Triage");
        var workflow = Change(1, "workflow", ["triage"], "Workflow");
        var set = ChangeSet(2, [role, workflow]);

        var differences = ResourcePlanReviewProjection.Differences(role);
        Assert.IsTrue(differences.Any(value => value.Path == "definition.displayName" && value.Proposed == "Triage"));
        var graph = ResourcePlanReviewProjection.Graph(set.Value);
        Assert.HasCount(2, graph.Nodes);
        Assert.HasCount(1, graph.Edges);
        Assert.AreEqual("triage", graph.Edges[0].From);
        Assert.AreEqual("workflow", graph.Edges[0].To);
        Assert.IsTrue(graph.Nodes[1].X > graph.Nodes[0].X);
    }

    [TestMethod]
    public void ReviewProjectionRejectsStaleRevisionAndDigest()
    {
        var set = ChangeSet(1, [Change(0, "triage", [], "Triage")]);
        var validation = Validation(set.Value, "old-digest");

        Assert.IsTrue(ResourcePlanReviewProjection.IsStale(Plan, set.Value));
        Assert.IsTrue(ResourcePlanReviewProjection.IsStale(Plan, set.Value, validation));
        Assert.AreEqual(set, ResourcePlanReviewProjection.LatestChangeSet(Plan, [set]));
    }

    [TestMethod]
    public void DetailRendersFiveViewsAndKeepsStaleReviewReadOnly()
    {
        using var culture = new TestCultureScope("en-US");
        var set = ChangeSet(1, [Change(0, "triage", [], "Triage")]);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourcePlansApiClient>(new FakeClient(set));
        context.Services.AddSingleton(new ConsoleContextState(new FakeContextProvider()));
        context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default).GetAwaiter().GetResult();

        var rendered = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(5, rendered.FindAll("[role=tab]").Count);
            Assert.Contains("Support design", rendered.Markup, StringComparison.Ordinal);
            Assert.Contains("ChangeSet belongs to an earlier revision", rendered.Markup, StringComparison.Ordinal);
            Assert.IsTrue(rendered.FindAll("button").Single(value => value.TextContent.Contains("Revalidate", StringComparison.Ordinal)).HasAttribute("disabled"));
        });
        rendered.Find("#tab-Changes").Click();
        Assert.Contains("definition.displayName", rendered.Markup, StringComparison.Ordinal);
        rendered.Find("#tab-Graph").Click();
        Assert.AreEqual(1, rendered.FindAll(".topology-node").Count);
        Assert.AreEqual(0, rendered.FindAll(".topology-legend").Count);
        rendered.Find(".topology-node").Click();
        Assert.Contains("Resource type", rendered.Find(".resource-plan-graph-details").TextContent, StringComparison.Ordinal);
        Assert.Contains("triage", rendered.Find(".resource-plan-graph-details").TextContent, StringComparison.Ordinal);
        rendered.Find("#tab-Validation").Click();
        Assert.Contains("Validation is stale", rendered.Markup, StringComparison.Ordinal);
        rendered.Find("#tab-Activity").Click();
        Assert.Contains("/flow-runs/run-1", rendered.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void DetailRevalidatesCurrentChangeSetAndFrenchLabelsAreAvailable()
    {
        using var culture = new TestCultureScope("fr-FR");
        var set = ChangeSet(Plan.Revision, [Change(0, "triage", [], "Triage")]);
        var client = new FakeClient(set);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourcePlansApiClient>(client);
        context.Services.AddSingleton(new ConsoleContextState(new FakeContextProvider()));
        context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default).GetAwaiter().GetResult();

        var rendered = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        rendered.WaitForAssertion(() => Assert.Contains("Plans de ressources", context.Services.GetRequiredService<IStringLocalizer<ResourcePlansStrings>>()["Title"].Value, StringComparison.Ordinal));
        rendered.FindAll("button").Single(value => value.TextContent.Contains("Revalider", StringComparison.Ordinal)).Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, client.ValidationCalls);
            Assert.Contains("Validation du ChangeSet enregistrée", rendered.Markup, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void DetailShowsBlockedModelProfileIssueAfterRevalidation()
    {
        using var culture = new TestCultureScope("fr-FR");
        var set = ChangeSet(Plan.Revision, [Change(0, "triage", [], "Triage")]);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourcePlansApiClient>(new FakeClient(set, blockOnValidate: true));
        context.Services.AddSingleton(new ConsoleContextState(new FakeContextProvider()));
        context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default).GetAwaiter().GetResult();

        var rendered = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        rendered.WaitForElement("#tab-Validation");
        rendered.FindAll("button").Single(value => value.TextContent.Contains("Revalider", StringComparison.Ordinal)).Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.Contains("resource_change_model_profile_invalid", rendered.Markup, StringComparison.Ordinal);
            Assert.Contains("changes[0].proposed.definition.modelProfile", rendered.Markup, StringComparison.Ordinal);
            Assert.Contains("ModelProfile 'default/default'", rendered.Markup, StringComparison.Ordinal);
        });
    }

    private static ResourceChange Change(int order, string logicalId, IReadOnlyList<string> dependsOn, string displayName)
    {
        var proposed = new PlannedResourceDocument("agentstration.io/v1", "Agent", ResourceScopeRef.Workspace(Scope.WorkspaceId.Value),
            new ResourceMetadata { Name = logicalId }, JsonSerializer.SerializeToElement(new { displayName }));
        var current = new CurrentResourceEvidence(null, 1, "\"old\"", JsonSerializer.SerializeToElement(new
        {
            apiVersion = "agentstration.io/v1",
            kind = "Agent",
            scopeRef = ResourceScopeRef.Workspace(Scope.WorkspaceId.Value),
            metadata = new { name = logicalId },
            definition = new { displayName = "Old name" }
        }), "old");
        return new(order, logicalId, ResourceChangeOperation.Update, proposed, current, dependsOn, "new");
    }

    private static ResourceChangeSetSnapshot ChangeSet(long revision, IReadOnlyList<ResourceChange> changes) => new(
        new(new ResourceChangeSetId(Guid.NewGuid()), Plan.Id, revision, Scope, ResourcePlanningContractVersions.V1, "1.0.0", "materialized", "digest",
            ResourceChangeSetStatus.Proposed, changes, [], Guid.NewGuid(), DateTimeOffset.UnixEpoch), "\"set\"");

    private static ResourceChangeSetValidation Validation(ResourceChangeSet set, string digest) => new(
        Guid.NewGuid(), set.Id, digest, set.PlanId, set.PlanRevision, Scope, ResourceChangeSetReadiness.Ready, [], Guid.NewGuid(), DateTimeOffset.UnixEpoch);

    private sealed class FakeClient(ResourceChangeSetSnapshot set, bool blockOnValidate = false) : IResourcePlansApiClient
    {
        private IReadOnlyList<ResourceChangeSetValidation> validations = [Validation(set.Value, "old-digest")];
        public int ValidationCalls { get; private set; }
        public Task<ResourcePlanPage> ListPlansAsync(ResourcePlanStatus? status, int skip, int take, CancellationToken cancellationToken) => Task.FromResult(new ResourcePlanPage([new(Plan, "\"plan\"")], false));
        public Task<ResourcePlanSnapshot?> GetPlanAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ResourcePlanSnapshot?>(id == Plan.Id.Value ? new(Plan, "\"plan\"") : null);
        public Task<IReadOnlyList<ResourcePlanActivity>> ListActivitiesAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourcePlanActivity>>([new(Guid.NewGuid(), Plan.Id, Scope, 1, ResourcePlanActivityType.Created, Guid.NewGuid(), null, DateTimeOffset.UnixEpoch)]);
        public Task<ResourcePlanMaterialization> MaterializeAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceChangeSetPage> ListChangeSetsAsync(Guid planId, int skip, int take, CancellationToken cancellationToken) => Task.FromResult(new ResourceChangeSetPage([set], false));
        public Task<ResourceChangeSetSnapshot> CreateChangeSetAsync(Guid planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceChangeSetValidation>> ListValidationsAsync(Guid changeSetId, CancellationToken cancellationToken) => Task.FromResult(validations);
        public Task<ResourceChangeSetValidation> ValidateChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken)
        {
            ValidationCalls++;
            var result = Validation(set.Value, set.Value.Digest) with
            {
                ValidatedAt = DateTimeOffset.UtcNow,
                Readiness = blockOnValidate ? ResourceChangeSetReadiness.Blocked : ResourceChangeSetReadiness.Ready,
                Issues = blockOnValidate ? [new("resource_change_model_profile_invalid", "changes[0].proposed.definition.modelProfile", "Agent 'triage' references ModelProfile 'default/default'.")] : []
            };
            validations = [.. validations, result];
            return Task.FromResult(result);
        }
    }

    private sealed class FakeContextProvider : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Reviewer", Scope.TenantId, "tenant", "Tenant", Scope.WorkspaceId.Value, "workspace", "Workspace",
            new HashSet<string>(["resources/read", "resources/write"], StringComparer.Ordinal), []));
    }
}
