using System.Net;
using System.Net.Http.Json;
using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.ModelProfiles;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentResourceTemplateTests
{
    [TestMethod]
    [DataRow("en-US", "Identity", "Value or resource")]
    [DataRow("fr-FR", "Identité", "Valeur ou ressource")]
    public async Task SavedOverviewAndContentLocalActionsPreserveDraftAndSaveToTheExistingResource(string locale, string identity, string valueHeader)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("[data-testid=agent-resource-details]")));
        Assert.AreEqual(identity, cut.Find("[data-testid=agent-identity-summary] h2").TextContent);
        Assert.AreEqual(valueHeader, cut.Find("[data-testid=agent-configuration-summary] th:last-child").TextContent);
        Assert.Contains(handler.Current.Definition.Instructions, cut.Find("[data-testid=agent-configuration-summary]").TextContent);
        Assert.Contains("scopeRef=%2Finstance", cut.Find("[data-testid=agent-configuration-summary] a[href*='/modelprofiles/']").GetAttribute("href")!);
        Assert.IsEmpty(cut.FindAll("[role=switch]"), "Agent has no enablement property.");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        Assert.HasCount(1, cut.FindAll(".resource-header .required-fields-note"));
        Assert.IsEmpty(cut.FindAll("form .required-fields-note"));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        cut.Find("[data-testid=agent-display-name]").Input("Unsaved agent name");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));
        Assert.Contains("Saved agent", cut.Find("[data-testid=agent-identity-summary]").TextContent);
        Assert.DoesNotContain("Unsaved agent name", cut.Find("[data-testid=agent-identity-summary]").TextContent);
        Assert.IsNotNull(cut.Find(".resource-details-main > .unsaved-changes-bar"));
        Assert.AreEqual("agent-resource-form", cut.Find("[data-testid=agent-save-draft]").GetAttribute("form"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreEqual("Unsaved agent name", cut.Find("[data-testid=agent-display-name]").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll(".validation-errors"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));
        cut.Find("[data-testid=agent-save-draft]").Click();
        // bUnit does not implement a browser's external form-owner submission.
        await cut.Find("#agent-resource-form").SubmitAsync();
        cut.WaitForAssertion(() => Assert.AreEqual("Unsaved agent name", handler.Current.Definition.DisplayName));
        Assert.AreEqual("\"initial\"", handler.LastEtag);
        Assert.AreEqual(ResourceScopeRef.Instance, handler.LastRequest!.ScopeRef);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=agent-instructions]").Change(string.Empty);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));
        await cut.Find("#agent-resource-form").SubmitAsync();
        Assert.AreEqual("page", cut.Find("a[data-resource-section='definition/configuration']").GetAttribute("aria-current"));
        Assert.HasCount(1, cut.FindAll(".validation-message"));
        Assert.AreEqual("Unsaved agent name", handler.Current.Definition.DisplayName);
        cut.Find("[data-testid=agent-discard]").Click();
        Assert.AreEqual(handler.Current.Definition.Instructions, cut.Find("[data-testid=agent-instructions]").GetAttribute("value"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/tools"));
        Assert.IsEmpty(cut.FindAll(".required-fields-note"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DiscardFromYamlRestoresSavedManifestAndFormWithoutLeavingTheSection(bool includeFormDraft)
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("[data-testid=agent-resource-details]")));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("yaml"));
        var savedYaml = cut.Find("[data-testid=agent-yaml-editor] textarea").GetAttribute("value");
        Assert.IsFalse(string.IsNullOrWhiteSpace(savedYaml));
        if (includeFormDraft)
        {
            await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
            await cut.Find("[data-testid=agent-display-name]").InputAsync("Unsaved form name");
            await cut.InvokeAsync(() => navigation.NavigateSection("yaml"));
        }
        await cut.Find("[data-testid=agent-yaml-editor] textarea").InputAsync(
            includeFormDraft ? "invalid: [ YAML draft" : savedYaml + "\n# unsaved YAML comment");
        Assert.IsNotNull(cut.Find("[data-testid=agent-discard]"));
        await cut.Find("[data-testid=agent-discard]").ClickAsync();
        Assert.AreEqual("yaml", navigation.CurrentPath);
        Assert.AreEqual(savedYaml, cut.Find("[data-testid=agent-yaml-editor] textarea").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsNull(handler.LastRequest, "Discard must not save the resource.");
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        Assert.AreEqual(handler.Current.Definition.DisplayName, cut.Find("[data-testid=agent-display-name]").GetAttribute("value"));
        await cut.InvokeAsync(() => navigation.NavigateSection("yaml"));
        Assert.AreEqual(savedYaml, cut.Find("[data-testid=agent-yaml-editor] textarea").GetAttribute("value"));
    }

    [TestMethod]
    public void LoadingASavedResourceDoesNotCreateADraftOrBlockInitialNormalization()
    {
        using var handler = new AgentHandler { ModelLookupGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        handler.ModelLookupGate.SetResult();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("[data-testid=agent-overview]")));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task ASlowInitialLoadCannotResetTheSectionOrDraftAfterContextNormalization()
    {
        using var handler = new AgentHandler { ModelLookupGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<ObservedAgentEditor>(p => p.Add(c => c.Name, "welcome"));
        var initialLoad = cut.Instance.LatestLoad;
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/agents/welcome?context=%2Finstance#definition/configuration");
        cut.Render(p => p.Add(c => c.Name, "welcome"));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("[data-testid=agent-display-name]")));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        cut.Find("[data-testid=agent-display-name]").Input("Retained after context normalization");
        handler.ModelLookupGate.SetResult();
        await initialLoad;
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreEqual("Retained after context normalization", cut.Find("[data-testid=agent-display-name]").GetAttribute("value"));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    public sealed class ObservedAgentEditor : AgentEditor
    {
        public Task LatestLoad { get; private set; } = Task.CompletedTask;
        protected override Task OnParametersSetAsync() => LatestLoad = base.OnParametersSetAsync();
    }

    [TestMethod]
    public async Task InheritedDefinitionStaysReadOnlyAndHasNoDraftActions()
    {
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http, ResourceScopeRef.Workspace(Guid.NewGuid()));
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        Assert.IsTrue(cut.Find("[data-testid=agent-definition-fields]").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar, [data-resource-section=access]"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        Assert.IsTrue(cut.Find("[data-testid=agent-yaml-editor] textarea").HasAttribute("readonly"));
        Assert.IsNull(handler.LastRequest);
    }

    private static BunitContext Context(HttpClient http, ResourceScopeRef? scope = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/configuration", "definition/model-execution", "definition/tools", "definition/contracts", "yaml", "access" })
        {
            var target = "http://localhost/agents/welcome#" + section;
            module.Setup<string>("navigate", invocation => Equals(invocation.Arguments[0], target)).SetResult(target);
        }
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<NotificationState>();
        context.Services.AddSingleton<IManagementApiClient>(new ManagementApiClient(http));
        context.Services.AddSingleton<IAgentsModelClient>(new AgentsModelApiClient(http));
        context.Services.AddSingleton<IAgentRunnerRuntimeClient>(new RuntimeApiClient(http));
        context.Services.AddSingleton<IRuntimeProfilesClient>(new RuntimeProfilesApiClient(http));
        context.Services.AddSingleton<IToolsClient>(new ToolsApiClient(http));
        context.Services.AddSingleton<IToolSetsClient>(new ToolSetsApiClient(http));
        context.Services.AddSingleton<IToolCategoriesClient>(new ToolCategoriesApiClient(http));
        context.ComponentFactories.AddStub<ModelProfilePicker>();
        context.ComponentFactories.AddStub<ResourceScopeFormField>();
        if (scope is { } owner) context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext(owner));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/agents/welcome");
        return context;
    }

    private sealed class NavigationContext(ResourceScopeRef scope) : IConsoleNavigationContext
    {
        public string? ScopeRef => scope.Value;
        public string AddToUri(string uri) => uri;
    }

    private sealed class AgentHandler : HttpMessageHandler
    {
        public TaskCompletionSource? ModelLookupGate { get; init; }
        private int modelLookups;
        public AgentResource Current { get; private set; } = new()
        {
            Uid = Guid.NewGuid(),
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.Agent,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "welcome" },
            Generation = 1,
            Definition = new() { DisplayName = "Saved agent", Instructions = "Read the entire instruction. " + new string('x', 180), ModelProfile = new("reasoning-default", ResourceScopeRef.Instance) }
        };
        public string? LastEtag { get; private set; }
        public AgentResourceRequest? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/model", StringComparison.Ordinal) && Interlocked.Increment(ref modelLookups) == 1 && ModelLookupGate is not null)
                await ModelLookupGate.Task.WaitAsync(cancellationToken);
            if (path == "/api/agents/welcome")
            {
                if (request.Method == HttpMethod.Put)
                {
                    LastEtag = request.Headers.IfMatch.Single().ToString();
                    LastRequest = (await request.Content!.ReadFromJsonAsync<AgentResourceRequest>(cancellationToken))!;
                    Current = Current with { Definition = LastRequest.Definition, Generation = Current.Generation + 1 };
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Current) };
                response.Headers.ETag = new(request.Method == HttpMethod.Put ? "\"updated\"" : "\"initial\"");
                return response;
            }
            if (path is "/api/tools" or "/api/toolproviders" or "/api/toolcategories" or "/api/toolsets" or "/api/runtimeprofiles")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { value = Array.Empty<object>() }) };
            return new(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { title = "Not deployed", status = 404 }) };
        }
    }
}
