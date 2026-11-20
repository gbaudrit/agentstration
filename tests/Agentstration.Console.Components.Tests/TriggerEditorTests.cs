using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
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
public sealed class TriggerEditorTests
{
    [TestMethod]
    [DataRow("en-US", ConsoleContextKind.Instance, "Instance")]
    [DataRow("fr-FR", ConsoleContextKind.Instance, "Instance")]
    [DataRow("en-US", ConsoleContextKind.Tenant, "Tenant")]
    [DataRow("fr-FR", ConsoleContextKind.Tenant, "Tenant")]
    [DataRow("en-US", ConsoleContextKind.Workspace, "Workspace")]
    [DataRow("fr-FR", ConsoleContextKind.Workspace, "Espace de travail")]
    public void CreationAndReviewShowTheLogicalScopeWhileTheFormKeepsItsPhysicalOwner(string culture, ConsoleContextKind kind, string label)
    {
        using var locale = new TestCultureScope(culture);
        using var api = new TriggerHandler();
        using var context = Context(api, kind: kind);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/triggers/new?namespace=schedules");
        var page = context.Render<TriggerEditor>();
        page.WaitForElement("[data-testid=trigger-form]");
        Assert.AreEqual(LogicalScope(kind), page.FindComponent<PageHeader>().Instance.ScopeRef);
        var owner = page.Find("[data-testid=resource-scope-readonly]");
        Assert.IsTrue(owner.HasAttribute("readonly"));
        Assert.AreEqual(kind == ConsoleContextKind.Instance ? label : $"{label} · {(kind == ConsoleContextKind.Tenant ? "Development" : "Project workspace")}", owner.GetAttribute("value"));
        Assert.DoesNotContain("operations", owner.GetAttribute("value")!);
        Assert.AreEqual(ResourceScopeRef.Workspace(Workspace), ((TriggerEditorModel)page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.Model).ScopeRef);
        page.Find("[data-wizard-step=review]").Click();
        Assert.AreEqual(kind == ConsoleContextKind.Instance ? label : $"{label} · {(kind == ConsoleContextKind.Tenant ? "Development" : "Project workspace")}", page.Find(".resource-identity-summary .resource-scope-text").TextContent);
        Assert.IsEmpty(page.FindAll(".resource-identity-summary .resource-scope-badge"));
        Assert.DoesNotContain("operations", page.Find(".resource-identity-summary").TextContent);
        Assert.AreEqual(0, api.Writes);
    }

    [TestMethod]
    public void CreationUsesTheWizardAndTheActualFixedOperationalOwnerWithoutAnInventorySelector()
    {
        using var culture = new TestCultureScope("en-US"); using var api = new TriggerHandler(); using var context = Context(api);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/triggers/new?namespace=schedules");
        var rendered = context.Render<TriggerEditor>(); rendered.WaitForElement("[data-testid=trigger-form]");
        CollectionAssert.AreEqual(new[] { "identity", "target", "schedule", "input", "review" }, rendered.FindAll("[data-wizard-step]").Select(value => value.GetAttribute("data-wizard-step")).ToArray());
        Assert.AreEqual(ResourceScopeRef.Workspace(Workspace), rendered.FindComponent<ResourceIdentityEditor>().Instance.ScopeRef);
        Assert.HasCount(0, rendered.FindComponents<ResourceScopePicker>());
        Assert.AreEqual("schedules", rendered.FindComponent<ResourceDefinitionForm>().Instance.EditContext.Model is TriggerEditorModel model ? model.Namespace : null);
        rendered.Find("[data-wizard-step=review]").Click();
        rendered.Find("[data-testid=trigger-create]").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual("identity", rendered.FindComponent<CreationWizard>().Instance.CurrentStepId));
        Assert.AreEqual(0, api.Writes);
        Assert.IsTrue(api.PreviewCounts.All(value => value == 5));
    }

    [TestMethod]
    public void FreeStepNavigationKeepsIncompleteAndInvalidFieldsAndRoutesTheFirstActualError()
    {
        using var culture = new TestCultureScope("en-US"); using var api = new TriggerHandler(); using var context = Context(api);
        var rendered = context.Render<TriggerEditor>(); rendered.WaitForElement("[data-testid=trigger-display-name]").Input("Scheduled contract");
        rendered.Find("[data-wizard-step=target]").Click(); rendered.Find("[data-testid=trigger-flow]").Change(Target);
        rendered.Find("[data-wizard-step=input]").Click(); rendered.Find("[data-testid=trigger-input]").Input("{ invalid");
        rendered.Find("[data-wizard-step=review]").Click(); rendered.Find("[data-testid=trigger-create]").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual("input", rendered.FindComponent<CreationWizard>().Instance.CurrentStepId));
        Assert.AreEqual("{ invalid", rendered.Find("[data-testid=trigger-input]").GetAttribute("value"));
        Assert.AreEqual(0, api.Writes);
        rendered.Find("[data-wizard-step=schedule]").Click();
        var form = rendered.FindComponent<ResourceDefinitionForm>().Instance;
        var timeZone = rendered.FindAll("select").Single(value => value.QuerySelector("option[value='Europe/Paris']") is not null);
        timeZone.Change(""); rendered.Find("[data-wizard-step=review]").Click();
        Assert.AreSame(form.EditContext, rendered.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("review", rendered.FindComponent<CreationWizard>().Instance.CurrentStepId);
    }

    [TestMethod]
    public async Task InvalidJsonReturnsToItsStepAfterAnActuallyPendingServerPreview()
    {
        using var api = new TriggerHandler(); using var context = Context(api); var rendered = context.Render<TriggerEditor>();
        rendered.WaitForElement("[data-testid=trigger-display-name]").Input("Deferred preview");
        rendered.Find("[data-testid=trigger-flow]").Change(Target); rendered.Find("[data-testid=trigger-input]").Input("{ invalid");
        rendered.Find("[data-wizard-step=review]").Click();
        var pending = new TaskCompletionSource<HttpResponseMessage>(); api.PendingPreview = pending.Task;
        var finish = rendered.Find("[data-testid=trigger-create]").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.IsTrue(api.PreviewWaiting));
        Assert.IsTrue(rendered.FindComponent<ResourceDefinitionForm>().Instance.Busy);
        Assert.IsFalse(rendered.FindComponent<CreationWizard>().Instance.Busy, "The wizard owns its in-progress finish lock; a mirrored Busy value can be stale when the callback returns.");
        await rendered.InvokeAsync(() => pending.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new { occurrences = Array.Empty<DateTimeOffset>() }) }));
        await finish;
        rendered.WaitForAssertion(() => Assert.AreEqual("input", rendered.FindComponent<CreationWizard>().Instance.CurrentStepId));
        Assert.AreEqual("{ invalid", rendered.Find("[data-testid=trigger-input]").GetAttribute("value"));
        Assert.AreEqual(0, api.Writes);
    }

    [TestMethod]
    public void ActualCreationKeepsTheNamespaceAndCreatesOnlyADisabledTrigger()
    {
        using var api = new TriggerHandler(); using var context = Context(api);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/triggers/new?namespace=schedules");
        var rendered = context.Render<TriggerEditor>(); rendered.WaitForElement("[data-testid=trigger-display-name]").Input("Scheduled contract");
        rendered.Find("[data-testid=trigger-flow]").Change(Target);
        rendered.Find("[data-wizard-step=review]").Click(); rendered.Find("[data-testid=trigger-create]").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(api.Saved));
        Assert.AreEqual("scheduled-contract", api.Saved!.Name); Assert.AreEqual("schedules", api.Saved.Namespace.Value);
        Assert.AreEqual(ResourceScopeRef.Workspace(Workspace), api.Saved.ScopeRef); Assert.IsFalse(api.Saved.Definition.Enabled);
        Assert.IsNull(api.Saved.Definition.ExecutionScope); Assert.IsNull(api.IfMatch); Assert.AreEqual(1, api.Writes);
        Assert.AreEqual(ResourceScopeRef.Instance, api.Saved.Definition.Target.Flow!.ScopeRef);
        Assert.AreEqual(ResourceNamespace.Parse("contracts"), api.Saved.Definition.Target.Flow.Namespace);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MetadataSaveKeepsOriginalEtagNamespaceTagsAndTheWholePinnedTarget(bool relativeNamespace)
    {
        using var api = new TriggerHandler(); api.Resource = api.Resource with { Definition = api.Resource.Definition with { Target = new() { Flow = api.Resource.Definition.Target.Flow! with { Namespace = relativeNamespace ? null : ResourceNamespace.Parse("contracts") } } } };
        using var context = Context(api); var rendered = Existing(context, api);
        rendered.WaitForElement("[data-testid=trigger-display-name]").Input("Changed metadata");
        await rendered.InvokeAsync(rendered.Instance.SubmitAsync);
        rendered.WaitForAssertion(() => Assert.IsNotNull(api.Saved));
        Assert.AreEqual("\"original\"", api.IfMatch);
        Assert.AreEqual(api.Resource.Uid, api.Saved!.Uid); Assert.AreEqual("schedules", api.Saved.Namespace.Value);
        CollectionAssert.AreEquivalent(api.Resource.Metadata.Tags.ToArray(), api.Saved.Metadata.Tags.ToArray());
        Assert.AreEqual(api.Resource.Definition.Target.Flow, api.Saved.Definition.Target.Flow);
        Assert.AreEqual(api.Resource.Definition.ExecutionScope, api.Saved.Definition.ExecutionScope);
        Assert.AreEqual("Changed metadata", api.Saved.Definition.DisplayName);
    }

    [TestMethod]
    public async Task ExplicitTargetReselectionRequestsResolutionInsteadOfSilentlyKeepingAnOldPin()
    {
        using var api = new TriggerHandler(); using var context = Context(api); var rendered = Existing(context, api);
        rendered.WaitForElement("[data-testid=trigger-flow]").Change("");
        rendered.Find("[data-testid=trigger-flow]").Change(Target);
        await rendered.InvokeAsync(rendered.Instance.SubmitAsync);
        rendered.WaitForAssertion(() => Assert.IsNotNull(api.Saved));
        Assert.AreEqual("published-flow", api.Saved!.Definition.Target.Flow!.Name);
        Assert.AreEqual(ResourceScopeRef.Instance, api.Saved.Definition.Target.Flow.ScopeRef);
        Assert.IsNull(api.Saved.Definition.Target.Flow.Version);
        Assert.IsNull(api.Saved.Definition.Target.Flow.Uid);
        Assert.IsNull(api.Saved.Definition.Target.Flow.DefinitionHash);
        Assert.AreEqual("\"original\"", api.IfMatch);
    }

    [TestMethod]
    public async Task OwningLayoutInstancesAndInvalidRawDraftSurviveIncomingSnapshotsUntilExplicitDiscard()
    {
        using var api = new TriggerHandler(); using var context = Context(api);
        var state = new ResourceSectionState(); state.Select("definition/configuration"); var presentations = TriggerEditor.DefinitionPresentations();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/triggers/schedule/edit?namespace=schedules");
        var rendered = context.Render<TriggerEditor>(p => p.Add(value => value.Name, "schedule").Add(value => value.SectionState, state).Add(value => value.SectionPresentations, presentations));
        rendered.WaitForElement("[data-testid=trigger-input]").Input("{ retained");
        var form = rendered.FindComponent<ResourceDefinitionForm>().Instance; var originalContext = form.EditContext;
        Assert.AreSame(state, form.SectionState); Assert.AreSame(presentations, form.Sections);
        var newer = new ResourceSnapshot<TriggerResource>(api.Resource with { Definition = api.Resource.Definition with { DisplayName = "New server view" } }, "\"newer\"");
        await rendered.InvokeAsync(() => rendered.Instance.ApplySnapshot(newer));
        Assert.AreSame(originalContext, rendered.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("\"original\"", rendered.Instance.Version);
        Assert.AreEqual("{ retained", rendered.Find("[data-testid=trigger-input]").GetAttribute("value"));
        state.Select("definition/identity"); rendered.Render(); state.Select("definition/configuration"); rendered.Render();
        Assert.AreSame(originalContext, rendered.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        await rendered.InvokeAsync(() => rendered.Instance.ApplySnapshot(newer, discard: true));
        Assert.AreEqual("\"newer\"", rendered.Instance.Version);
        Assert.AreNotSame(originalContext, rendered.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsFalse(rendered.Instance.IsDirty);
    }

    [TestMethod]
    public async Task ActivationAcknowledgementUpdatesOnlyTheSavedEnvelopeAndEtagWhileKeepingInvalidInput()
    {
        using var api = new TriggerHandler(); using var context = Context(api); var rendered = Existing(context, api);
        rendered.WaitForElement("[data-testid=trigger-input]").Input("{ pending");
        var form = rendered.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await rendered.InvokeAsync(() => rendered.Instance.ApplyOperationalSnapshot(new(api.Resource with { Definition = api.Resource.Definition with { Enabled = true } }, "\"enabled\"")));
        Assert.AreSame(form, rendered.FindComponent<ResourceDefinitionForm>().Instance.EditContext); Assert.IsTrue(rendered.Instance.IsDirty);
        Assert.AreEqual("\"enabled\"", rendered.Instance.Version); Assert.AreEqual("{ pending", rendered.Find("[data-testid=trigger-input]").GetAttribute("value"));
        rendered.Find("[data-testid=trigger-input]").Input("{}"); await rendered.InvokeAsync(rendered.Instance.SubmitAsync);
        rendered.WaitForAssertion(() => Assert.IsNotNull(api.Saved)); Assert.IsTrue(api.Saved!.Definition.Enabled); Assert.AreEqual("\"enabled\"", api.IfMatch);
    }

    [TestMethod]
    public async Task MissingCatalogKeepsTheSavedExactBindingEditableAndARejectedWriteKeepsItsDraft()
    {
        using var api = new TriggerHandler { FlowFailure = true, WriteFailure = true }; using var context = Context(api); var rendered = Existing(context, api);
        rendered.WaitForElement("[data-testid=trigger-display-name]").Input("Retained after conflict");
        await rendered.InvokeAsync(rendered.Instance.SubmitAsync);
        rendered.WaitForElement("[data-testid=trigger-error]"); Assert.AreEqual("Retained after conflict", rendered.Find("[data-testid=trigger-display-name]").GetAttribute("value"));
        Assert.AreEqual("\"original\"", rendered.Instance.Version); Assert.IsTrue(rendered.Instance.IsDirty);
        Assert.AreEqual(api.Resource.Definition.Target.Flow, api.Saved!.Definition.Target.Flow); Assert.AreEqual(1, api.Writes);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, ResourceLoadStatus.Forbidden)]
    [DataRow(HttpStatusCode.NotFound, ResourceLoadStatus.NotFound)]
    [DataRow(HttpStatusCode.ServiceUnavailable, ResourceLoadStatus.Failed)]
    public void TypedReadFailuresDoNotExposeContentOrLoadAnAuxiliaryCatalog(HttpStatusCode failure, ResourceLoadStatus state)
    {
        using var api = new TriggerHandler { ReadFailure = failure }; using var context = Context(api); var rendered = Existing(context, api);
        rendered.WaitForAssertion(() => Assert.AreEqual(state, rendered.FindComponent<ResourceLoadFeedback>().Instance.State.Status));
        Assert.HasCount(0, rendered.FindAll("[data-testid=trigger-form]")); Assert.AreEqual(0, api.FlowReads);
    }

    [TestMethod]
    public void AForeignPhysicalOwnerCannotBecomeAnEditableTriggerOrExposeItsAuxiliaryCatalog()
    {
        using var api = new TriggerHandler(); api.Resource = api.Resource with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) };
        using var context = Context(api); var rendered = Existing(context, api);
        rendered.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.Forbidden, rendered.FindComponent<ResourceLoadFeedback>().Instance.State.Status));
        Assert.HasCount(0, rendered.FindAll("[data-testid=trigger-form]")); Assert.AreEqual(0, api.FlowReads);
    }

    [TestMethod]
    public void AReadOnlyActorCanInspectButCannotSaveEvenIfAControlEventIsForged()
    {
        using var api = new TriggerHandler(); using var context = Context(api, false); var rendered = Existing(context, api);
        rendered.WaitForElement("[data-testid=trigger-form]"); Assert.IsTrue(rendered.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        rendered.Find("[data-testid=trigger-display-name]").Input("Forbidden write"); rendered.Find("form").Submit();
        Assert.AreEqual(0, api.Writes);
    }

    private static readonly Guid Workspace = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Target = "/instance|contracts|published-flow";
    private static IRenderedComponent<TriggerEditor> Existing(BunitContext context, TriggerHandler api)
    {
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/triggers/schedule/edit?namespace=schedules");
        return context.Render<TriggerEditor>(p => p.Add(value => value.Name, "schedule"));
    }
    private static readonly Guid Tenant = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static ResourceScopeRef LogicalScope(ConsoleContextKind kind) => kind switch { ConsoleContextKind.Instance => ResourceScopeRef.Instance, ConsoleContextKind.Tenant => ResourceScopeRef.Tenant(Tenant), _ => ResourceScopeRef.Workspace(Workspace) };
    private static BunitContext Context(TriggerHandler handler, bool write = true, ConsoleContextKind kind = ConsoleContextKind.Instance)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        context.Services.AddSingleton<ITriggerApiClient>(new TriggerApiClient(http)); context.Services.AddSingleton<IFlowApiClient>(new FlowApiClient(http));
        context.Services.AddSingleton(new ConsoleContextState(new Provider(write, kind))); context.Services.AddSingleton<NotificationState>(); context.Services.AddSingleton<TimeProvider>(new Clock());
        return context;
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero); }
    private sealed class Provider(bool write, ConsoleContextKind kind) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken token) => Task.FromResult(new ConsoleContextSnapshot(Guid.NewGuid(), "Operator", LogicalScope(kind).Value, kind, Tenant, "dev", "Development", Workspace, "fixture-workspace", kind == ConsoleContextKind.Workspace ? "Project workspace" : kind == ConsoleContextKind.Tenant ? "Tenant operations" : "Instance operations", new HashSet<string>(write ? [AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite] : [AuthorizationPermissions.ResourcesRead]), [], []));
    }
    private sealed class TriggerHandler : HttpMessageHandler
    {
        public TriggerResource Resource { get; set; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = TriggerResourceKinds.Trigger,
            Metadata = new() { Name = "schedule", Namespace = ResourceNamespace.Parse("schedules"), Tags = new Dictionary<string, string> { ["team"] = "retained" } },
            ScopeRef = ResourceScopeRef.Workspace(Workspace),
            Uid = Guid.NewGuid(),
            Generation = 4,
            Definition = new() { DisplayName = "Saved schedule", Source = new() { Schedule = new() { Type = TriggerScheduleType.Interval, Every = "PT6H", StartAt = new Clock().GetUtcNow().AddHours(1), TimeZone = "Europe/Paris" } }, Target = new() { Flow = new() { Name = "published-flow", Namespace = ResourceNamespace.Parse("contracts"), ScopeRef = ResourceScopeRef.Instance, Version = "2.3.4", Uid = Guid.NewGuid(), DefinitionHash = "sha256:exact-published-definition" } }, ExecutionScope = new(Guid.NewGuid(), Workspace, Guid.NewGuid()) { LogicalScopeRef = ResourceScopeRef.Instance } }
        };
        public HttpStatusCode? ReadFailure { get; set; }
        public bool FlowFailure { get; set; }
        public bool WriteFailure { get; set; }
        public int FlowReads { get; private set; }
        public int Writes { get; private set; }
        public string? IfMatch { get; private set; }
        public TriggerResource? Saved { get; private set; }
        public List<int> PreviewCounts { get; } = [];
        public Task<HttpResponseMessage>? PendingPreview { get; set; }
        public bool PreviewWaiting { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/flows")
            {
                ++FlowReads; if (FlowFailure) return Error(HttpStatusCode.ServiceUnavailable);
                return Ok(new FlowPageResponse([new("published-flow", "Published Flow", null, FlowKind.Workflow, "1.0.0", true, "1.0.0", new Clock().GetUtcNow()) { Namespace = ResourceNamespace.Parse("contracts"), ScopeRef = ResourceScopeRef.Instance }], null));
            }
            if (path == "/api/triggers/schedule-preview") { if (PendingPreview is { } pending) { PendingPreview = null; PreviewWaiting = true; return await pending; } using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); PreviewCounts.Add(data.RootElement.GetProperty("count").GetInt32()); return Ok(new { occurrences = new[] { new Clock().GetUtcNow().AddHours(1) } }); }
            if (request.Method == HttpMethod.Put)
            {
                ++Writes; Saved = await request.Content!.ReadFromJsonAsync<TriggerResource>(token); IfMatch = request.Headers.IfMatch.FirstOrDefault()?.Tag;
                if (WriteFailure) return Error(HttpStatusCode.PreconditionFailed);
                return Ok(Saved! with { Uid = Saved!.Uid == Guid.Empty ? Guid.NewGuid() : Saved.Uid }, "\"saved\"");
            }
            if (ReadFailure is { } failure) return Error(failure);
            return Ok(Resource, "\"original\"");
        }
        private static HttpResponseMessage Ok<T>(T value, string? etag = null) { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) }; if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag); return response; }
        private static HttpResponseMessage Error(HttpStatusCode status) => new(status) { Content = JsonContent.Create(new { title = "Expected failure", status = (int)status }) };
    }
}
