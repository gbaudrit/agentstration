using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Triggers;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class TriggerDetailsFoundationTests
{
    [TestMethod]
    [DataRow("/instance")]
    [DataRow("/tenants/33333333-3333-3333-3333-333333333333")]
    public void LegacyFlowScopeLinksAreCanonicalizedToTheSavedTriggerOwner(string legacyOwner)
    {
        using var api = new TriggerHandler();
        api.Resource = api.Resource with { Definition = api.Resource.Definition with {
            Target = api.Resource.Definition.Target with { Flow = api.Resource.Definition.Target.Flow! with { ScopeRef = ResourceScopeRef.Parse(legacyOwner) } }
        } };
        using var context = Context(api);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.Uri + "&scopeRef=" + Uri.EscapeDataString(legacyOwner) + "#yaml");
        var page = context.Render<TriggerDetails>(p => p.Add(v => v.Name, "schedule"));
        page.WaitForAssertion(() =>
        {
            var location = new Uri(navigation.Uri);
            Assert.AreEqual(api.Resource.ScopeRef!.Value.Value, Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query)["scopeRef"].ToString());
            Assert.AreEqual("/instance", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query)["context"].ToString());
            Assert.AreEqual("#yaml", location.Fragment);
        });
    }

    [TestMethod]
    public void AnExplicitDifferentPhysicalOwnerCannotOpenTheCurrentContextsHomonym()
    {
        using var api = new TriggerHandler(); using var context = Context(api);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.Uri + "&scopeRef=" + Uri.EscapeDataString(ResourceScopeRef.Workspace(Guid.NewGuid()).Value));
        var page = context.Render<TriggerDetails>(p => p.Add(v => v.Name, "schedule"));
        page.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.NotFound, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status));
        Assert.IsNull(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.IsEmpty(page.FindAll("[data-testid=trigger-display-name]"));
    }

    [TestMethod]
    [DataRow("en-US", ConsoleContextKind.Instance, "Instance")]
    [DataRow("fr-FR", ConsoleContextKind.Instance, "Instance")]
    [DataRow("en-US", ConsoleContextKind.Tenant, "Tenant")]
    [DataRow("fr-FR", ConsoleContextKind.Tenant, "Tenant")]
    [DataRow("en-US", ConsoleContextKind.Workspace, "Workspace")]
    [DataRow("fr-FR", ConsoleContextKind.Workspace, "Espace de travail")]
    public async Task HeaderAndIdentityPresentTheLogicalScopeWithoutChangingTheStoredOwner(string culture, ConsoleContextKind kind, string label)
    {
        using var locale = new TestCultureScope(culture);
        using var api = new TriggerHandler();
        using var context = Context(api, kind: kind);
        var page = Render(context);
        page.WaitForElement("[data-testid=trigger-identity-summary]");
        var logical = LogicalScope(kind);
        Assert.AreEqual(ResourceScopeRef.Workspace(Workspace), page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource!.ScopeRef);
        Assert.AreEqual(logical, page.FindComponent<ResourceDetailsTemplate>().Instance.ScopeRef);
        Assert.IsNull(page.FindComponent<ResourceDetailsTemplate>().Instance.DefinitionReadOnlyReason);
        var badgeLabel = kind == ConsoleContextKind.Instance ? "Instance" : $"{(kind == ConsoleContextKind.Tenant ? "Tenant" : "Workspace")}{(culture == "fr-FR" ? " : " : ": ")}{(kind == ConsoleContextKind.Tenant ? "Development" : "Project workspace")}";
        Assert.AreEqual(badgeLabel, page.Find(".resource-header .resource-scope-badge").TextContent);
        Assert.HasCount(1, page.FindAll(".resource-header .resource-scope-badge"));
        Assert.AreEqual("Version 4", page.Find(".resource-header-badges .resource-version-badge").TextContent);
        Assert.AreEqual(culture == "fr-FR" ? "Version de configuration" : "Configuration version", page.Find(".resource-version-badge").GetAttribute("title"));
        Assert.DoesNotContain("Generation", page.Find(".resource-overview-metrics").TextContent);
        Assert.DoesNotContain("Génération", page.Find(".resource-overview-metrics").TextContent);
        Assert.IsEmpty(page.FindAll("[data-testid=trigger-definition-scope]"));
        var identity = page.Find("[data-testid=trigger-identity-summary]");
        Assert.IsNull(identity.QuerySelector(".resource-scope-text"), "Saved ownership is already shown in the header.");
        Assert.IsNull(identity.QuerySelector(".resource-scope-badge"));
        Assert.DoesNotContain("operations", identity.TextContent);
        Assert.AreEqual(LogicalScope(kind), page.FindComponent<ResourceIdentityPopover>().Instance.ScopeRef);
        await Select(page, "definition/identity");
        var owner = page.Find("[data-testid=resource-scope-readonly]");
        Assert.IsTrue(owner.HasAttribute("readonly"));
        Assert.AreEqual(kind == ConsoleContextKind.Instance ? label : $"{label} · {(kind == ConsoleContextKind.Tenant ? "Development" : "Project workspace")}", owner.GetAttribute("value"));
        Assert.DoesNotContain("operations", owner.GetAttribute("value")!);
        Assert.AreEqual(ResourceScopeRef.Workspace(Workspace), page.FindComponent<ResourceIdentityEditor>().Instance.ScopeRef);
        await Select(page, "yaml");
        StringAssert.Contains(page.Find("[data-testid=trigger-yaml-viewer]").GetAttribute("value")!, ResourceScopeRef.Workspace(Workspace).Value);
    }

    [TestMethod]
    public async Task CommonFrameKeepsActualOwnerTagsOrderedSectionsAndTheSameDefinitionStateAndFields()
    {
        using var api = new TriggerHandler(); using var context = Context(api); var page = Render(context);
        page.WaitForElement("[data-testid=trigger-identity-summary]");
        var frame = page.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.IsNull(page.FindComponent<ResourceDetailsTemplate>().Instance.DefinitionReadOnlyReason, "Logical Instance context must not lock its physical operational Workspace resource.");
        Assert.AreEqual(ResourceScopeRef.Workspace(Workspace), frame.Resource!.ScopeRef);
        StringAssert.Contains(page.Find("[data-testid=trigger-identity-summary]").TextContent, "reviewed");
        CollectionAssert.AreEqual(new[] { "overview", "definition/identity", "definition/configuration", "history", "execution", "access", "yaml" }, page.FindAll("[data-resource-section]").Select(value => value.GetAttribute("data-resource-section")).ToArray());
        Assert.IsTrue(page.Markup.IndexOf("resource-overview-metrics", StringComparison.Ordinal) < page.Markup.IndexOf("trigger-identity-summary", StringComparison.Ordinal));
        await Select(page, "definition/identity"); page.WaitForElement("[data-testid=trigger-display-name]");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(frame.SectionState, form.SectionState); Assert.AreSame(frame.SectionPresentations, form.Sections);
        Assert.IsEmpty(page.FindComponents<ResourceScopePicker>());
        await Select(page, "access"); StringAssert.Contains(page.Find("[data-testid=trigger-access-panel] a").GetAttribute("href")!, "#access");
        Assert.IsEmpty(page.FindComponents<ResourceAccessPanel>(), "Trigger access remains target Flow information, not a fabricated Trigger policy.");
        await Select(page, "yaml"); Assert.IsTrue(page.Find("[data-testid=trigger-yaml-viewer]").HasAttribute("readonly"));
        StringAssert.Contains(page.Find("[data-testid=trigger-yaml-viewer]").GetAttribute("value")!, "schedules");
        Assert.IsTrue(api.HistoryBounds.All(value => value == 100));
    }

    [TestMethod]
    public async Task InvalidRawAndEditContextSurviveSectionsUnchangedRefreshChangedRefreshAndExplicitDiscard()
    {
        using var api = new TriggerHandler(); using var context = Context(api); var page = Render(context);
        page.WaitForElement("[data-testid=trigger-identity-summary]"); await Select(page, "definition/configuration");
        page.WaitForElement("[data-testid=trigger-input]").Input("{ retained invalid");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await Refresh(page); Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        api.Resource = api.Resource with { Generation = 5, Definition = api.Resource.Definition with { DisplayName = "Changed on server" } }; api.Etag = "\"second\"";
        await Select(page, "overview"); await Refresh(page);
        Assert.AreEqual("Saved schedule", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Name);
        Assert.AreEqual("Version 4", page.Find(".resource-version-badge").TextContent);
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await Select(page, "definition/configuration"); Assert.AreEqual("{ retained invalid", page.Find("[data-testid=trigger-input]").GetAttribute("value"));
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.AreEqual("\"first\"", page.FindComponent<TriggerEditor>().Instance.Version);
        page.Find(".unsaved-changes-bar .button-secondary").Click();
        page.WaitForAssertion(() => Assert.AreEqual("Changed on server", page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Name));
        Assert.AreEqual("Version 5", page.Find(".resource-version-badge").TextContent);
        Assert.AreNotSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.AreEqual("\"second\"", page.FindComponent<TriggerEditor>().Instance.Version);
        Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task HeaderConfirmationAndActivationPreserveAnUnrelatedDraftAndTheAcknowledgedEtag()
    {
        using var api = new TriggerHandler(); using var context = Context(api); var page = Render(context);
        page.WaitForElement("[data-testid=trigger-enable]"); await Select(page, "definition/configuration"); page.WaitForElement("[data-testid=trigger-input]").Input("{ keep");
        var originalContext = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        page.Find("[data-testid=trigger-enable]").Change(true); Assert.AreEqual(0, api.Writes);
        page.Find("[data-testid=trigger-enable-dialog-confirm]").Click(); page.WaitForAssertion(() => Assert.AreEqual(1, api.Writes));
        Assert.AreEqual("\"first\"", api.IfMatch); Assert.IsTrue(api.Resource.Definition.Enabled);
        Assert.AreSame(originalContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("{ keep", page.Find("[data-testid=trigger-input]").GetAttribute("value")); Assert.AreEqual("\"saved\"", page.FindComponent<TriggerEditor>().Instance.Version);
        page.Find("[data-testid=trigger-input]").Input("{}"); await page.Find("[data-testid=trigger-form]").SubmitAsync();
        page.WaitForAssertion(() => Assert.AreEqual(2, api.Writes)); Assert.AreEqual("\"saved\"", api.IfMatch); Assert.IsTrue(api.Resource.Definition.Enabled);
    }

    [TestMethod]
    public async Task AsyncParserFailureRequestsFieldFocusAfterTheActualFormUnlocks()
    {
        using var api = new TriggerHandler(); using var context = Context(api);
        var focus = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-form.js").Setup<bool>("focusFirstInvalid", _ => true); focus.SetResult(true);
        var page = Render(context); page.WaitForElement("[data-testid=trigger-enable]"); await Select(page, "definition/configuration");
        page.WaitForElement("[data-testid=trigger-input]").Input("{ invalid"); var original = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        var pending = new TaskCompletionSource<HttpResponseMessage>(); api.PendingPreview = pending.Task;
        var submission = page.Find("[data-testid=trigger-form]").SubmitAsync();
        page.WaitForAssertion(() => Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.Busy));
        await page.InvokeAsync(() => pending.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new { occurrences = Array.Empty<DateTimeOffset>() }) })); await submission;
        page.WaitForAssertion(() => Assert.IsGreaterThan(0, focus.Invocations.Count));
        Assert.AreSame(original, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.IsFalse(page.FindComponent<ResourceDefinitionForm>().Instance.Busy);
        Assert.IsTrue(original.GetValidationMessages(original.Field(nameof(TriggerEditorModel.InputJson))).Any()); Assert.AreEqual(0, api.Writes);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExecutionAndTargetAccessRemainIndependentOfManagedOrPermissionReadOnlyDefinitions(bool managed)
    {
        using var api = new TriggerHandler(); api.Resource = api.Resource with { Definition = api.Resource.Definition with { Enabled = true }, Governance = managed ? ResourceGovernance.Core("fixture") : api.Resource.Governance };
        using var context = Context(api, write: managed, execute: true); var page = Render(context); page.WaitForElement("[data-testid=trigger-run-now]");
        Assert.IsFalse(page.Find("[data-testid=trigger-run-now]").HasAttribute("disabled")); Assert.IsTrue(page.Find("[data-testid=trigger-enable]").HasAttribute("disabled"));
        Assert.IsEmpty(page.FindAll("[data-testid=trigger-delete]"));
        await Select(page, "access"); Assert.IsNotNull(page.Find("[data-testid=trigger-access-panel]"));
        await Select(page, "definition/identity"); page.WaitForElement("[data-testid=trigger-display-name]"); Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        page.Find("[data-testid=trigger-run-now]").Click(); page.WaitForAssertion(() => Assert.AreEqual(1, api.Runs)); Assert.AreEqual(0, api.Writes);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, ResourceLoadStatus.Forbidden)]
    [DataRow(HttpStatusCode.NotFound, ResourceLoadStatus.NotFound)]
    [DataRow(HttpStatusCode.ServiceUnavailable, ResourceLoadStatus.Failed)]
    public async Task RefreshFailuresClassifyPrivacyAndRetainRecoverableDrafts(HttpStatusCode status, ResourceLoadStatus expected)
    {
        using var api = new TriggerHandler(); using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=trigger-enable]");
        await Select(page, "definition/identity"); page.WaitForElement("[data-testid=trigger-display-name]").Input("Private pending"); var historyReads = api.HistoryBounds.Count;
        api.ReadFailure = status; await Refresh(page);
        Assert.AreEqual(expected, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status); Assert.AreEqual(historyReads, api.HistoryBounds.Count);
        if (status == HttpStatusCode.ServiceUnavailable) { Assert.AreEqual("Private pending", page.Find("[data-testid=trigger-display-name]").GetAttribute("value")); Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); }
        else { Assert.IsEmpty(page.FindAll("[data-testid=trigger-display-name]")); Assert.IsNull(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ForeignOwnerOrReplacedUidClearThePrivateViewBeforeHistory(bool ownerMismatch)
    {
        using var api = new TriggerHandler(); using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=trigger-enable]"); var reads = api.HistoryBounds.Count;
        api.Resource = ownerMismatch ? api.Resource with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } : api.Resource with { Uid = Guid.NewGuid() }; await Refresh(page);
        Assert.IsNull(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource); Assert.AreEqual(reads, api.HistoryBounds.Count);
    }

    [TestMethod]
    public async Task BoundedHistoryRejectsForeignOccurrencesAndShowsUnknownRatherThanAnInventedEmptyHistory()
    {
        using var culture = new TestCultureScope("en-US");
        using var api = new TriggerHandler { ForeignHistory = true }; using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=trigger-enable]"); await Select(page, "history");
        Assert.IsEmpty(page.FindAll("[data-testid=trigger-occurrence-row]")); StringAssert.Contains(page.Find("[data-testid=trigger-occurrences]").TextContent, "Occurrence history could not be loaded");
        Assert.IsTrue(api.HistoryBounds.All(value => value == 100));
    }

    [TestMethod]
    public async Task DeleteReleasesTheDraftGuardBeforeNavigatingAndPreservesTheOriginalEtag()
    {
        using var api = new TriggerHandler(); using var context = Context(api); var page = Render(context); page.WaitForElement("[data-testid=trigger-enable]"); await Select(page, "definition/identity"); page.WaitForElement("[data-testid=trigger-display-name]").Input("Pending delete");
        page.Find("[data-testid=trigger-delete]").Click(); page.Find("[data-testid=trigger-delete-dialog-confirm]").Click();
        page.WaitForAssertion(() => Assert.AreEqual(1, api.Deletes)); Assert.AreEqual("\"first\"", api.IfMatch); Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        StringAssert.Contains(context.Services.GetRequiredService<NavigationManager>().Uri, "/triggers");
    }

    private static Task Select(IRenderedComponent<TriggerDetails> page, string path) => page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection(path));
    private static Task Refresh(IRenderedComponent<TriggerDetails> page) => page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
    private static readonly Guid Workspace = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static IRenderedComponent<TriggerDetails> Render(BunitContext context) => context.Render<TriggerDetails>(p => p.Add(value => value.Name, "schedule"));
    private static readonly Guid Tenant = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static ResourceScopeRef LogicalScope(ConsoleContextKind kind) => kind switch { ConsoleContextKind.Instance => ResourceScopeRef.Instance, ConsoleContextKind.Tenant => ResourceScopeRef.Tenant(Tenant), _ => ResourceScopeRef.Workspace(Workspace) };
    private static BunitContext Context(TriggerHandler api, bool write = true, bool execute = false, ConsoleContextKind kind = ConsoleContextKind.Instance)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext(LogicalScope(kind).Value));
        var http = new HttpClient(api) { BaseAddress = new("http://localhost/") }; context.Services.AddSingleton<ITriggerApiClient>(new TriggerApiClient(http)); context.Services.AddSingleton<IFlowApiClient>(new FlowApiClient(http));
        context.Services.AddSingleton(new ConsoleContextState(new Provider(write, execute, kind))); context.Services.AddSingleton<NotificationState>(); context.Services.AddSingleton(TimeProvider.System);
        var navigation = context.Services.GetRequiredService<NavigationManager>(); navigation.NavigateTo("http://localhost/triggers/schedule?namespace=schedules&context=" + Uri.EscapeDataString(LogicalScope(kind).Value));
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Setup<string>("currentLocation", _ => true).SetResult(navigation.Uri);
        foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "access", "yaml", "history", "execution" }) { var url = navigation.Uri + "#" + path; module.Setup<string>("navigate", call => Equals(call.Arguments[0], url)).SetResult(url); }
        return context;
    }
    private sealed class NavigationContext(string scope) : IConsoleNavigationContext { public string? ScopeRef => scope; public string AddToUri(string value) => value; }
    private sealed class Provider(bool write, bool execute, ConsoleContextKind kind) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken token) => Task.FromResult(new ConsoleContextSnapshot(Guid.NewGuid(), "Operator", LogicalScope(kind).Value, kind, Tenant, "dev", "Development", Workspace, "fixture-workspace", kind == ConsoleContextKind.Workspace ? "Project workspace" : kind == ConsoleContextKind.Tenant ? "Tenant operations" : "Instance operations", new HashSet<string>(new[] { AuthorizationPermissions.ResourcesRead }.Concat(write ? [AuthorizationPermissions.ResourcesWrite] : []).Concat(execute ? [AuthorizationPermissions.RunsExecute] : [])), [], []));
    }
    private sealed class TriggerHandler : HttpMessageHandler
    {
        public TriggerResource Resource { get; set; } = new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = TriggerResourceKinds.Trigger, Metadata = new() { Name = "schedule", Namespace = ResourceNamespace.Parse("schedules"), Tags = new Dictionary<string, string> { ["team"] = "reviewed" } }, ScopeRef = ResourceScopeRef.Workspace(Workspace), Uid = Guid.NewGuid(), Generation = 4, Definition = new() { DisplayName = "Saved schedule", Input = JsonSerializer.SerializeToElement(new { parameter = "saved" }), Source = new() { Schedule = new() { Type = TriggerScheduleType.Interval, Every = "PT6H", StartAt = DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimeZone = "UTC" } }, Target = new() { Flow = new() { Name = "published-flow", Namespace = ResourceNamespace.Parse("contracts"), ScopeRef = ResourceScopeRef.Instance, Version = "2.3.4", Uid = Guid.NewGuid(), DefinitionHash = "sha256:published" } } } };
        public string Etag { get; set; } = "\"first\"";
        public HttpStatusCode? ReadFailure { get; set; }
        public bool ForeignHistory { get; set; }
        public Task<HttpResponseMessage>? PendingPreview { get; set; }
        public int Writes { get; private set; }
        public int Runs { get; private set; }
        public int Deletes { get; private set; }
        public string? IfMatch { get; private set; }
        public List<int> HistoryBounds { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/flows") return Ok(new Agentstration.Flows.Contracts.FlowPageResponse([], null));
            if (path.EndsWith("schedule-preview", StringComparison.Ordinal)) { if (PendingPreview is { } pending) { PendingPreview = null; return await pending.WaitAsync(token); } return Ok(new { occurrences = Array.Empty<DateTimeOffset>() }); }
            if (path.EndsWith("occurrences", StringComparison.Ordinal)) { HistoryBounds.Add(int.Parse(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["take"]!, System.Globalization.CultureInfo.InvariantCulture)); return Ok(ForeignHistory ? new[] { Occurrence() with { TriggerUid = Guid.NewGuid() } } : Array.Empty<TriggerOccurrence>()); }
            if (path.EndsWith("/run", StringComparison.Ordinal)) { ++Runs; return Ok(Occurrence()); }
            if (request.Method != HttpMethod.Get) IfMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null;
            if (request.Method == HttpMethod.Delete) { ++Deletes; return new(HttpStatusCode.NoContent); }
            if (request.Method == HttpMethod.Put) { ++Writes; Resource = (await request.Content!.ReadFromJsonAsync<TriggerResource>(token))!; Etag = "\"saved\""; }
            else if (ReadFailure is { } failure) return new(failure) { Content = System.Net.Http.Json.JsonContent.Create(new { title = "read_failed" }) };
            var response = Ok(Resource); response.Headers.TryAddWithoutValidation("ETag", Etag); return response;
        }
        private TriggerOccurrence Occurrence() => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), WorkspaceId = Workspace, TriggerUid = Resource.Uid, TriggerName = Resource.Name, TriggerNamespace = Resource.Namespace, TriggerGeneration = Resource.Generation, Kind = TriggerOccurrenceKind.Manual, ScheduledAt = DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), Outcome = TriggerOccurrenceOutcome.Submitted };
        private static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(value) };
    }
}
