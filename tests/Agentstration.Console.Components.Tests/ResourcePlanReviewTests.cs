using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
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
    private static readonly Guid AppliedPrincipalId = Guid.NewGuid();
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
    public void ReviewProjectionKeepsReadableDefinitionChangesAndLocatesAffectedResource()
    {
        var change = Change(0, "triage", [], "Triage");
        var set = ChangeSet(Plan.Revision, [change]);
        var issue = new ResourceChangeSetValidationIssue("resource_change_model_profile_invalid", "changes[0].proposed.definition.modelProfile", "Missing profile");

        Assert.AreEqual("Triage", ResourcePlanReviewProjection.DisplayName(change));
        CollectionAssert.AreEqual(new[] { "definition.displayName" }, ResourcePlanReviewProjection.ReviewFields(change).Select(value => value.Path).ToArray());
        Assert.AreEqual(change, ResourcePlanReviewProjection.ChangeForIssue(set.Value, issue));
        Assert.AreEqual("Rédaction, Analyse", ResourcePlanReviewProjection.FormatValue("[\"Rédaction\",\"Analyse\"]"));
        Assert.AreEqual("default/ticketing", ResourcePlanReviewProjection.FormatValue("[{\"name\":\"ticketing\",\"namespace\":\"default\"}]"));

        var flow = change with
        {
            Proposed = change.Proposed with
            {
                Kind = "Flow",
                Definition = JsonSerializer.SerializeToElement(new
                {
                    displayName = "Routing",
                    version = "1.0.0",
                    enabled = true,
                    spec = new { flowKind = "orchestration", pattern = new { strategy = "sequential" } },
                    publish = true,
                    activate = true
                })
            }
        };
        CollectionAssert.AreEqual(new[] { "definition.spec.flowKind", "definition.spec.pattern.strategy", "definition.version" },
            ResourcePlanReviewProjection.HighlightFields(flow).Select(value => value.Path).ToArray());
    }

    [TestMethod]
    public void DetailRendersSixViewsAndKeepsStaleReviewReadOnly()
    {
        using var culture = new TestCultureScope("en-US");
        var set = ChangeSet(1, [Change(0, "triage", [], "Triage")]);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourcePlansApiClient>(new FakeClient(set));
        RegisterProfiles(context);
        context.Services.AddSingleton(new ConsoleContextState(new FakeContextProvider()));
        context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default).GetAwaiter().GetResult();

        var rendered = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(6, rendered.FindAll("[role=tab]").Count);
            Assert.Contains("Support design", rendered.Markup, StringComparison.Ordinal);
            Assert.Contains("ChangeSet belongs to an earlier revision", rendered.Markup, StringComparison.Ordinal);
        });
        rendered.Find("#tab-Changes").Click();
        Assert.IsTrue(rendered.FindAll("button").Single(value => value.TextContent.Contains("Check proposal", StringComparison.Ordinal)).HasAttribute("disabled"));
        Assert.Contains("definition.displayName", rendered.Markup, StringComparison.Ordinal);
        Assert.AreEqual(1, rendered.FindAll(".resource-plan-change").Count);
        Assert.Contains("Compare 1 field", rendered.Find(".resource-plan-change-diff summary").TextContent, StringComparison.Ordinal);
        rendered.Find("#tab-Graph").Click();
        Assert.AreEqual(1, rendered.FindAll(".topology-node").Count);
        Assert.AreEqual(0, rendered.FindAll(".topology-legend").Count);
        rendered.Find(".topology-node").Click();
        Assert.Contains("Resource type", rendered.Find(".resource-plan-graph-details").TextContent, StringComparison.Ordinal);
        Assert.Contains("triage", rendered.Find(".resource-plan-graph-details").TextContent, StringComparison.Ordinal);
        rendered.Find("#tab-Validation").Click();
        Assert.Contains("Verification is stale", rendered.Markup, StringComparison.Ordinal);
        rendered.Find("#tab-Activity").Click();
        Assert.Contains("Plan origin", rendered.Find(".resource-plan-origin-card").TextContent, StringComparison.Ordinal);
        Assert.Contains(Plan.Origin.PrincipalId.ToString("D"), rendered.Find(".resource-plan-origin").TextContent, StringComparison.Ordinal);
        Assert.IsFalse(rendered.Find(".resource-plan-origin").TextContent.Contains("Plan creator", StringComparison.Ordinal));
        Assert.Contains("Event history", rendered.Find(".resource-plan-history").TextContent, StringComparison.Ordinal);
        Assert.AreEqual(1, rendered.FindAll(".resource-plan-timeline li").Count);
        Assert.Contains("Created", rendered.Find(".resource-plan-timeline li").TextContent, StringComparison.Ordinal);
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
        RegisterProfiles(context);
        context.Services.AddSingleton(new ConsoleContextState(new FakeContextProvider()));
        context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default).GetAwaiter().GetResult();

        var rendered = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        rendered.WaitForAssertion(() => Assert.Contains("Plans de ressources", context.Services.GetRequiredService<IStringLocalizer<ResourcePlansStrings>>()["Title"].Value, StringComparison.Ordinal));
        Assert.Contains("Créer un profil de modèle pour continuer", rendered.Markup, StringComparison.Ordinal);
        rendered.Find("#tab-Validation").Click();
        rendered.FindAll("button").Single(value => value.TextContent.Contains("Vérifier la proposition", StringComparison.Ordinal)).Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, client.ValidationCalls);
            Assert.Contains("Prêt pour la suite", rendered.Find(".resource-plan-validation-summary").TextContent, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void ReadyVerificationCanApplyThePinnedProposal()
    {
        using var culture = new TestCultureScope("fr-FR");
        var set = ChangeSet(Plan.Revision, [Change(0, "triage", [], "Triage")]);
        var client = new FakeClient(set);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourcePlansApiClient>(client);
        RegisterProfiles(context);
        context.Services.AddSingleton(new ConsoleContextState(new FakeContextProvider(identityRead: true)));
        context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default).GetAwaiter().GetResult();

        var rendered = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        rendered.WaitForAssertion(() => Assert.Contains("Support design", rendered.Markup, StringComparison.Ordinal));
        rendered.Find("#tab-Activity").Click();
        Assert.Contains("Plan creator", rendered.Find(".resource-plan-origin").TextContent, StringComparison.Ordinal);
        Assert.IsFalse(rendered.Find(".resource-plan-origin").TextContent.Contains("Appliqué par", StringComparison.Ordinal));
        rendered.Find("#tab-Application").Click();
        Assert.Contains("Vérifiez d’abord la proposition", rendered.Find(".resource-plan-application-pending").TextContent, StringComparison.Ordinal);
        rendered.Find("#tab-Validation").Click();
        rendered.FindAll("button").Single(value => value.TextContent.Contains("Vérifier la proposition", StringComparison.Ordinal)).Click();
        rendered.WaitForAssertion(() => Assert.Contains("Prêt pour la suite", rendered.Find(".resource-plan-validation-summary").TextContent, StringComparison.Ordinal));
        Assert.IsEmpty(rendered.FindAll(".resource-plan-application-summary"));
        rendered.FindAll("button").Single(value => value.TextContent.Contains("Voir l’application", StringComparison.Ordinal)).Click();
        Assert.Contains("Appliquer la proposition", rendered.Markup, StringComparison.Ordinal);
        rendered.FindAll("button").Single(value => value.TextContent.Contains("Appliquer la proposition", StringComparison.Ordinal)).Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(client.LastApplyRequest);
            Assert.AreEqual(set.Value.Digest, client.LastApplyRequest.ChangeSetDigest);
            Assert.IsTrue(rendered.Find("#tab-Application").ClassList.Contains("active"));
            Assert.Contains("Ressources appliquées", rendered.Find(".resource-plan-application-summary").TextContent, StringComparison.Ordinal);
            Assert.AreEqual("1", rendered.Find(".resource-plan-application-counts strong").TextContent);
            Assert.Contains("Triage", rendered.Find(".resource-plan-application-operations").TextContent, StringComparison.Ordinal);
        });
        rendered.Find("#tab-Activity").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.Contains("Appliqué par", rendered.Find(".resource-plan-origin").TextContent, StringComparison.Ordinal);
            Assert.Contains("Aline Martin", rendered.Find(".resource-plan-origin").TextContent, StringComparison.Ordinal);
            Assert.Contains(client.AppliedPrincipalId.ToString("D"), rendered.Find(".resource-plan-origin").TextContent, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void DetailShowsBlockedModelProfileIssueAfterRevalidation()
    {
        using var culture = new TestCultureScope("fr-FR");
        var change = Change(0, "triage", [], "Triage");
        var set = ChangeSet(Plan.Revision, [change with { Proposed = change.Proposed with { Definition = JsonSerializer.SerializeToElement(new { displayName = "Triage", modelProfile = new { name = "default" } }) } }]);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourcePlansApiClient>(new FakeClient(set, blockOnValidate: true));
        RegisterProfiles(context);
        context.Services.AddSingleton(new ConsoleContextState(new FakeContextProvider()));
        context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default).GetAwaiter().GetResult();

        var rendered = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        rendered.WaitForElement("#tab-Validation");
        rendered.Find("#tab-Validation").Click();
        rendered.FindAll("button").Single(value => value.TextContent.Contains("Vérifier la proposition", StringComparison.Ordinal)).Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("Profil de modèle à configurer", rendered.Find(".resource-plan-issue-content h3").TextContent);
            Assert.Contains("profil de modèle « default/default »", rendered.Find(".resource-plan-issue-content").TextContent, StringComparison.Ordinal);
            Assert.Contains("Créez ou corrigez ce profil", rendered.Find(".resource-plan-issue-action").TextContent, StringComparison.Ordinal);
            Assert.IsFalse(rendered.Find(".resource-plan-issue-technical").HasAttribute("open"));
            Assert.Contains("point à corriger", rendered.Find(".resource-plan-validation-summary").TextContent, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void DetailPassesSelectedProfilesAndReviewedDigestToChangeSet()
    {
        using var culture = new TestCultureScope("en-US");
        var set = ChangeSet(Plan.Revision, [Change(0, "triage", [], "Triage"), Change(1, "workflow", ["triage"], "Workflow")]);
        var client = new FakeClient(set, supportMaterialization: true);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourcePlansApiClient>(client);
        RegisterProfiles(context, true);
        context.Services.AddSingleton(new ConsoleContextState(new FakeContextProvider()));
        context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default).GetAwaiter().GetResult();

        var rendered = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        rendered.WaitForElement(".resource-plan-binding-card");
        rendered.FindAll(".resource-plan-binding-card select")[0].Change("0");
        var partiallyReopened = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        partiallyReopened.WaitForAssertion(() => Assert.Contains("0 of 1 bindings configured", partiallyReopened.Markup, StringComparison.Ordinal));
        Assert.AreEqual("0", partiallyReopened.FindAll(".resource-plan-binding-card select")[0].GetAttribute("value"));
        Assert.AreEqual("", partiallyReopened.FindAll(".resource-plan-binding-card select")[1].GetAttribute("value"));
        rendered.FindAll(".resource-plan-binding-card select")[1].Change("0");
        rendered.WaitForAssertion(() => Assert.IsTrue(client.LastMaterializationRequest?.Bindings.Count == 1));
        var binding = client.LastMaterializationRequest!.Bindings.Single();
        Assert.AreEqual("triage", binding.LogicalId);
        Assert.AreEqual("model-a", binding.ModelProfile.Name);
        Assert.AreEqual("runtime-a", binding.RuntimeProfile.Name);
        Assert.AreEqual(ResourceScopeRef.Workspace(Scope.WorkspaceId.Value), binding.ModelProfile.ScopeRef);
        var reopened = context.Render<ResourcePlanDetails>(parameters => parameters.Add(value => value.Id, Plan.Id.Value));
        reopened.WaitForAssertion(() => Assert.Contains("1 of 1 bindings configured", reopened.Markup, StringComparison.Ordinal));
        Assert.AreEqual("0", reopened.FindAll(".resource-plan-binding-card select")[0].GetAttribute("value"));
        Assert.AreEqual("0", reopened.FindAll(".resource-plan-binding-card select")[1].GetAttribute("value"));
        reopened.Find("#tab-Changes").Click();
        Assert.Contains("Plan proposal", reopened.Markup, StringComparison.Ordinal);
        rendered.Find("#tab-Changes").Click();
        Assert.Contains("Plan proposal", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("model-a", rendered.Find(".resource-plan-change-highlights").TextContent, StringComparison.Ordinal);
        Assert.Contains("runtime-a", rendered.Find(".resource-plan-change-highlights").TextContent, StringComparison.Ordinal);
        rendered.Find("#tab-Graph").Click();
        Assert.AreEqual(1, rendered.FindAll(".topology-node").Count);
        rendered.Find("#tab-Changes").Click();
        rendered.Find("#change-set-select").Change(set.Value.Id.Value.ToString("D"));
        Assert.Contains("Saved proposal", rendered.Markup, StringComparison.Ordinal);
        Assert.AreEqual(2, rendered.FindAll(".resource-plan-change").Count);
        rendered.Find("#change-set-select").Change("preview");
        Assert.Contains("model-a", rendered.Find(".resource-plan-change-highlights").TextContent, StringComparison.Ordinal);
        rendered.FindAll("button").Single(value => value.TextContent.Contains("Save this proposal", StringComparison.Ordinal)).Click();
        rendered.WaitForAssertion(() => Assert.AreEqual("reviewed", client.LastChangeSetRequest?.ExpectedDigest));
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

    private static void RegisterProfiles(BunitContext context, bool withProfiles = false)
    {
        context.Services.AddSingleton<IModelProfilesClient>(new EmptyModelProfilesClient(withProfiles));
        context.Services.AddSingleton<IRuntimeProfilesClient>(new EmptyRuntimeProfilesClient(withProfiles));
        context.Services.AddSingleton<IToolsClient>(new EmptyToolsClient());
        context.Services.AddSingleton<IIdentityAdministrationApiClient>(new IdentityAdministrationApiClient(
            new HttpClient(new IdentityHandler()) { BaseAddress = new Uri("http://localhost/") }));
    }

    private sealed class IdentityHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tenant = new Tenant(Scope.TenantId, "test", "Test", TenantStatus.Active, DateTimeOffset.UnixEpoch);
            var members = new OrganizationMemberResponse[]
            {
                new(new Principal(Plan.Origin.PrincipalId, PrincipalKind.Human, "Plan creator", null, PrincipalStatus.Active, DateTimeOffset.UnixEpoch),
                    new TenantMembership(Guid.NewGuid(), Scope.TenantId, Plan.Origin.PrincipalId, MembershipStatus.Active, DateTimeOffset.UnixEpoch), [], []),
                new(new Principal(AppliedPrincipalId, PrincipalKind.Human, "Aline Martin", null, PrincipalStatus.Active, DateTimeOffset.UnixEpoch),
                    new TenantMembership(Guid.NewGuid(), Scope.TenantId, AppliedPrincipalId, MembershipStatus.Active, DateTimeOffset.UnixEpoch), [], [])
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new OrganizationAdministrationResponse(tenant, [], members))
            });
        }
    }

    private sealed class EmptyToolsClient : IToolsClient
    {
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolResource>>([]);
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyModelProfilesClient(bool withProfiles) : IModelProfilesClient
    {
        public Task<IReadOnlyList<ModelProfileSummaryResponse>> GetModelProfilesAsync(string? search, string? provider, string? status, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModelProfileSummaryResponse>>(
            withProfiles ? [new("model-a", "model-a", new("Model A", null, new("provider", "provider"), new("model"), new(), new(), new(), "available", 0), ScopeRef: ResourceScopeRef.Workspace(Scope.WorkspaceId.Value))] : []);
        public Task<ResourceSnapshot<ModelProfileResource>> GetModelProfileAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> CreateModelProfileAsync(CreateModelProfileRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> UpdateModelProfileAsync(string profileName, PutModelProfileRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteModelProfileAsync(string profileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProfileUsagesResponse> GetModelProfileUsagesAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelProfileResolutionResponse> GetModelProfileResolutionAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileOptionMigrationPreviewResponse>> PreviewOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ModelProfileResource>> ApplyOptionMigrationAsync(ResourceNamespace @namespace, string profileName, string targetVersion, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyRuntimeProfilesClient(bool withProfiles) : IRuntimeProfilesClient
    {
        public Task<IReadOnlyList<RuntimeProfileSummaryResponse>> GetRuntimeProfilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RuntimeProfileSummaryResponse>>(
            withProfiles ? [new("runtime-a", "runtime-a", new() { DisplayName = "Runtime A", RuntimeType = "maf" }, 0, ScopeRef: ResourceScopeRef.Workspace(Scope.WorkspaceId.Value))] : []);
        public Task<ResourceSnapshot<RuntimeProfileResource>> GetRuntimeProfileAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<RuntimeProfileResource>> CreateRuntimeProfileAsync(CreateRuntimeProfileRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<RuntimeProfileResource>> UpdateRuntimeProfileAsync(string profileName, PutRuntimeProfileRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRuntimeProfileAsync(string profileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeProfileUsagesResponse> GetRuntimeProfileUsagesAsync(string profileName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeClient(ResourceChangeSetSnapshot set, bool blockOnValidate = false, bool supportMaterialization = false) : IResourcePlansApiClient
    {
        private IReadOnlyList<ResourceChangeSetValidation> validations = [Validation(set.Value, "old-digest")];
        private ResourceChangeSetApplicationSnapshot? application;
        private ResourcePlanBindingDraftSnapshot? savedBindings;
        public Guid AppliedPrincipalId => ResourcePlanReviewTests.AppliedPrincipalId;
        public int ValidationCalls { get; private set; }
        public ResourcePlanMaterializationRequest? LastMaterializationRequest { get; private set; }
        public ResourcePlanMaterializationRequest? LastChangeSetRequest { get; private set; }
        public ApplyResourceChangeSetRequest? LastApplyRequest { get; private set; }
        public Task<ResourcePlanPage> ListPlansAsync(ResourcePlanStatus? status, int skip, int take, CancellationToken cancellationToken) => Task.FromResult(new ResourcePlanPage([new(Plan, "\"plan\"")], false));
        public Task<ResourcePlanSnapshot?> GetPlanAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ResourcePlanSnapshot?>(id == Plan.Id.Value ? new(Plan, "\"plan\"") : null);
        public Task<IReadOnlyList<ResourcePlanActivity>> ListActivitiesAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourcePlanActivity>>(application is null
            ? [new(Guid.NewGuid(), Plan.Id, Scope, 1, ResourcePlanActivityType.Created, Plan.Origin.PrincipalId, null, DateTimeOffset.UnixEpoch)]
            : [new(Guid.NewGuid(), Plan.Id, Scope, 1, ResourcePlanActivityType.Created, Plan.Origin.PrincipalId, null, DateTimeOffset.UnixEpoch),
                new(Guid.NewGuid(), Plan.Id, Scope, Plan.Revision, ResourcePlanActivityType.Applied, AppliedPrincipalId, null, DateTimeOffset.UnixEpoch.AddMinutes(1))]);
        public Task<ResourcePlanBindingDraftSnapshot?> GetBindingsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(savedBindings);
        public Task<ResourcePlanBindingDraftSnapshot> SaveBindingsAsync(Guid id, SaveResourcePlanBindingsRequest request, string? expectedETag, CancellationToken cancellationToken)
        {
            if (savedBindings?.ETag != expectedETag) throw new InvalidOperationException("stale bindings");
            savedBindings = new(new(Plan.Id, Scope, request.PlanRevision, request.Bindings, DateTimeOffset.UnixEpoch, request.IntegrationBindings), "\"saved\"");
            return Task.FromResult(savedBindings);
        }
        public Task<ResourcePlanMaterialization> MaterializeAsync(Guid id, ResourcePlanMaterializationRequest request, CancellationToken cancellationToken)
        {
            if (!supportMaterialization) throw new NotSupportedException();
            LastMaterializationRequest = request;
            var binding = request.Bindings.Single();
            var resource = new PlannedResourceDocument("agentstration.io/v1", "Agent", ResourceScopeRef.Workspace(Scope.WorkspaceId.Value),
                new ResourceMetadata { Name = binding.LogicalId }, JsonSerializer.SerializeToElement(new
                {
                    displayName = "Triage",
                    modelProfile = new { name = binding.ModelProfile.Name, @namespace = "default" },
                    runtimeProfile = new { name = binding.RuntimeProfile.Name, @namespace = "default" }
                }));
            ResourcePlanResolvedBinding[] evidence = [
                new(binding.LogicalId, "modelProfile", binding.ModelProfile, Guid.NewGuid(), 1, "model", "model-digest"),
                new(binding.LogicalId, "runtimeProfile", binding.RuntimeProfile, Guid.NewGuid(), 1, "runtime", "runtime-digest")];
            return Task.FromResult(new ResourcePlanMaterialization(Plan.Id, Plan.Revision, ResourcePlanningContractVersions.V1, "1.1.0", Scope,
                [new(binding.LogicalId, resource, ResourcePlanProposedOperation.Create, null, [], "proposed")], [], "reviewed", evidence));
        }
        public Task<ResourceChangeSetPage> ListChangeSetsAsync(Guid planId, int skip, int take, CancellationToken cancellationToken) => Task.FromResult(new ResourceChangeSetPage([set], false));
        public Task<ResourceChangeSetSnapshot> CreateChangeSetAsync(Guid planId, ResourcePlanMaterializationRequest request, CancellationToken cancellationToken)
        {
            if (!supportMaterialization) throw new NotSupportedException();
            LastChangeSetRequest = request;
            return Task.FromResult(set);
        }
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
        public Task<ResourceChangeSetApplicationSnapshot?> GetApplicationAsync(Guid changeSetId, CancellationToken cancellationToken) => Task.FromResult(application);
        public Task<ResourceChangeSetApplicationSnapshot> ApplyChangeSetAsync(Guid changeSetId, ApplyResourceChangeSetRequest request, CancellationToken cancellationToken)
        {
            LastApplyRequest = request;
            var now = DateTimeOffset.UtcNow;
            application = new(new(Guid.NewGuid(), request.PlanId, request.PlanRevision, new(changeSetId), request.ChangeSetDigest,
                request.ValidationId, Scope, ResourceChangeSetApplicationStatus.Applied,
                [new ResourceChangeApplicationOperation(0, "triage", ResourceChangeOperation.Update, ResourceChangeApplicationOutcome.Applied, Guid.NewGuid(), 2, "\"updated\"", null, null, now)],
                1, AppliedPrincipalId, now, now, now, now), "\"application\"");
            return Task.FromResult(application);
        }
    }

    private sealed class FakeContextProvider(bool identityRead = false) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Reviewer", Scope.TenantId, "tenant", "Tenant", Scope.WorkspaceId.Value, "workspace", "Workspace",
            new HashSet<string>(identityRead ? ["resources/read", "resources/write", "authorization/read"] : ["resources/read", "resources/write"], StringComparer.Ordinal), []));
    }
}
