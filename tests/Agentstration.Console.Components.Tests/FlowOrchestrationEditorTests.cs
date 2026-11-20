using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Triggers;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowOrchestrationEditorTests
{
    [TestMethod]
    public async Task SavedMetadataRetainsIdentityReferencesAndFreshActivationEtagWhileEditing()
    {
        using var handler = new FlowHandler();
        using var context = Context(handler);
        var rendered = Render(context);
        rendered.WaitForElement("[data-testid=flow-orchestration-editor]");
        rendered.Find("[data-testid=flow-definition-display-name]").Input("Pending display identity");
        Assert.IsTrue(rendered.Instance.IsDirty);
        var activated = handler.Resource with { Enabled = false };
        handler.Resource = activated; handler.ETag = "\"activation-etag\"";
        await rendered.InvokeAsync(() => rendered.Instance.ApplyActivationSnapshot(new(activated, handler.ETag)));
        Assert.AreEqual("Pending display identity", rendered.Find("[data-testid=flow-definition-display-name]").GetAttribute("value"));
        rendered.Find("form").Submit();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Updated));
        Assert.AreEqual("Pending display identity", handler.Updated!.DisplayName);
        Assert.IsFalse(handler.Updated.Enabled);
        Assert.AreEqual("preserved-tag", handler.Updated.Metadata!["tag"]);
        Assert.AreEqual("\"activation-etag\"", handler.WriteETag);
        CollectionAssert.AreEqual(new[] { "agent-a", "agent-b" }, ((OrchestrationFlowDefinition)handler.Updated.Definition).Participants.Select(value => value.Id).ToArray());
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Instance.IsDirty));
        Assert.AreEqual(1, handler.SnapshotReads);
    }

    [TestMethod]
    public async Task ParticipantReorderingNotifiesDraftOwnershipAndDiscardRestoresTheSavedOrder()
    {
        using var handler = new FlowHandler();
        using var context = Context(handler);
        var changes = new List<bool>();
        var rendered = context.Render<FlowOrchestrationEditor>(p => p.Add(value => value.FlowId, "review").Add(value => value.FlowNamespace, "team")
            .Add(value => value.ScopeRef, Owner).Add(value => value.Embedded, true).Add(value => value.DraftChanged, (bool dirty) => changes.Add(dirty)));
        rendered.WaitForElement(".participant-order");
        rendered.Find(".participant-order li:first-child .order-actions button:last-child").Click();
        rendered.WaitForAssertion(() => Assert.IsTrue(changes.Last()));
        Assert.AreEqual("Agent B", rendered.Find(".participant-order li:first-child .order-copy strong").TextContent);
        await rendered.InvokeAsync(rendered.Instance.Discard);
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Instance.IsDirty));
        Assert.AreEqual("Agent A", rendered.Find(".participant-order li:first-child .order-copy strong").TextContent);
        Assert.IsNull(handler.Updated);
    }

    [TestMethod]
    public void InvalidVersionStaysInlineAndPublicationSavesBeforePublishingTheExplicitVersion()
    {
        using var handler = new FlowHandler();
        using var context = Context(handler);
        var rendered = Render(context);
        rendered.WaitForElement("[data-testid=flow-definition-version]").Change("invalid");
        rendered.Find("form").Submit();
        rendered.WaitForElement(".validation-message");
        Assert.IsNull(handler.Updated);
        rendered.Find("[data-testid=flow-definition-version]").Change("1.2.0");
        rendered.Find("[data-testid=flow-publish-version]").Change("2.3.0");
        rendered.Find("[data-testid=flow-publish-and-activate]").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual("2.3.0", rendered.Find("[data-testid=flow-status-message]").GetAttribute("data-published-version")));
        CollectionAssert.AreEqual(new[] { "save", "publish", "read" }, handler.Commands);
        Assert.AreEqual("2.3.0", handler.Published!.Version);
        Assert.AreEqual("2.3.0", handler.Updated!.Version);
        Assert.IsTrue(handler.Published.Activate);
        Assert.AreEqual("2.3.0", handler.Resource.ActiveVersion);
        Assert.AreEqual(handler.Uid, handler.Resource.Uid);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ManagedOrInheritedDefinitionsCannotWriteAndManagedReadsTheExactPublishedSnapshot(bool managed)
    {
        using var handler = new FlowHandler();
        if (managed) handler.Resource = handler.Resource with { Governance = ResourceGovernance.Core("orchestration-fixture") };
        using var context = Context(handler, current: managed ? Owner : ResourceScopeRef.Instance);
        var rendered = Render(context);
        rendered.WaitForElement("[data-testid=flow-orchestration-editor]");
        Assert.IsFalse(rendered.Instance.CanSubmit);
        Assert.IsTrue(rendered.Find("fieldset.resource-definition-fields").HasAttribute("disabled"));
        Assert.IsEmpty(rendered.FindAll("[data-testid=flow-publish-and-activate]"));
        Assert.AreEqual(managed ? 1 : 0, handler.VersionReads);
        Assert.AreEqual(managed ? "Published description" : "Saved description", rendered.Find("[data-testid=flow-definition-description]").GetAttribute("value"));
        rendered.Find("form").Submit();
        Assert.IsNull(handler.Updated);
    }

    [TestMethod]
    public void StandaloneAliasRedirectsOnceToTheExactCanonicalDefinition()
    {
        using var handler = new FlowHandler();
        using var context = Context(handler);
        var rendered = context.Render<FlowOrchestrationEditor>(p => p.Add(value => value.FlowId, "review").Add(value => value.FlowNamespace, "team").Add(value => value.ScopeRef, Owner));
        var nav = context.Services.GetRequiredService<NavigationManager>();
        rendered.WaitForAssertion(() => Assert.AreEqual("#definition/configuration", new Uri(nav.Uri).Fragment));
        Assert.AreEqual("/namespaces/team/flows/review", new Uri(nav.Uri).AbsolutePath);
        Assert.AreEqual(Owner.Value, QueryHelpers.ParseQuery(new Uri(nav.Uri).Query)["scopeRef"].ToString());
        rendered.Render(p => p.Add(value => value.FlowId, "review").Add(value => value.FlowNamespace, "team").Add(value => value.ScopeRef, Owner));
        Assert.AreEqual(1, handler.SnapshotReads);
    }

    private static readonly ResourceScopeRef Owner = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static IRenderedComponent<FlowOrchestrationEditor> Render(BunitContext context) => context.Render<FlowOrchestrationEditor>(p =>
        p.Add(value => value.FlowId, "review").Add(value => value.FlowNamespace, "team").Add(value => value.ScopeRef, Owner).Add(value => value.Embedded, true));
    private static BunitContext Context(FlowHandler handler, ResourceScopeRef? current = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IFlowApiClient>(new FlowApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost/") }));
        context.Services.AddSingleton<IManagementApiClient>(new ManagementStub());
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext(current ?? Owner));
        return context;
    }
    private sealed class NavigationContext(ResourceScopeRef current) : IConsoleNavigationContext
    {
        public string? ScopeRef => current.Value;
        public string AddToUri(string uri) => uri;
    }
    private sealed class ManagementStub : IManagementApiClient
    {
        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentSummary>>([
            new("agent-a", "Agent A", "assistant", "1", "Ready", [], "local", DateTimeOffset.UnixEpoch, "default") { Namespace = new("team"), ScopeRef = Owner },
            new("agent-b", "Agent B", "assistant", "1", "Ready", [], "local", DateTimeOffset.UnixEpoch, "default") { Namespace = new("team"), ScopeRef = Owner }]);
        public Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TriggerResource>> GetTriggersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class FlowHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public Guid Uid { get; } = Guid.NewGuid();
        public FlowResponse Resource { get; set; }
        public string ETag { get; set; } = "\"original-etag\"";
        public string? WriteETag { get; private set; }
        public UpdateFlowRequest? Updated { get; private set; }
        public CreateFlowVersionRequest? Published { get; private set; }
        public int SnapshotReads { get; private set; }
        public int VersionReads { get; private set; }
        public List<string> Commands { get; } = [];
        public FlowHandler()
        {
            Resource = new("review", "review", "Saved description", "1.0.0", true, "1.0.0",
                new OrchestrationFlowDefinition([new(FlowTargetKind.Agent, "agent-a"), new(FlowTargetKind.Agent, "agent-b")], new SequentialOrchestrationPattern(true)),
                new Dictionary<string, string> { ["tag"] = "preserved-tag" }, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            { Uid = Uid, ScopeRef = Owner, Namespace = new("team"), DisplayName = "Review team" };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.AreEqual(Owner.Value, query["scopeRef"].ToString());
            if (request.Method == HttpMethod.Get && path.EndsWith("/versions/1.0.0", StringComparison.Ordinal))
            {
                VersionReads++;
                return Ok(new FlowVersionResponse("review", "1.0.0", "Published description", Resource.Definition, Resource.Metadata, DateTimeOffset.UnixEpoch) { ScopeRef = Owner, Namespace = new("team") });
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/versions", StringComparison.Ordinal))
            {
                Commands.Add("publish"); Published = await request.Content!.ReadFromJsonAsync<CreateFlowVersionRequest>(Json, cancellationToken);
                Assert.AreEqual(Resource.Version, Published!.Version, "Publication must match the persisted definition version.");
                Resource = Resource with { ActiveVersion = Published!.Version };
                return Ok(new FlowVersionResponse("review", Published.Version, Resource.Description, Resource.Definition, Resource.Metadata, DateTimeOffset.UnixEpoch) { ScopeRef = Owner, Namespace = new("team") });
            }
            if (request.Method == HttpMethod.Put)
            {
                Commands.Add("save"); Updated = await request.Content!.ReadFromJsonAsync<UpdateFlowRequest>(Json, cancellationToken); WriteETag = request.Headers.IfMatch.Single().Tag;
                Resource = Resource with { Description = Updated!.Description, DisplayName = Updated.DisplayName, Definition = Updated.Definition, Enabled = Updated.Enabled, Version = Updated.Version, Metadata = Updated.Metadata! };
                ETag = "\"saved-etag\""; return Ok(Resource);
            }
            if (request.Method == HttpMethod.Get)
            {
                SnapshotReads++; if (Published is not null) Commands.Add("read"); return Ok(Resource);
            }
            return new(HttpStatusCode.NotFound);
        }
        private HttpResponseMessage Ok<T>(T value)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) };
            response.Headers.ETag = new(ETag); return response;
        }
    }
}
