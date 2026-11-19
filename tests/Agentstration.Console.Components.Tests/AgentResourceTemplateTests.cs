using System.Net;
using System.Net.Http.Json;
using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.ModelProfiles;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AgentResourceTemplateTests
{
    [TestMethod]
    [DataRow("en-US", "Identity", "Labels")]
    [DataRow("fr-FR", "Identité", "Étiquettes")]
    public async Task IdentityOwnsLabelsAndInvalidHiddenLabelsReturnToIdentityWithoutLosingTheDraft(string locale, string identity, string labels)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        var emptyLabels = locale == "fr-FR" ? "Aucune étiquette" : "No labels";
        Assert.Contains(emptyLabels, cut.Find("[data-testid=agent-identity-summary] .labels-summary").TextContent);
        Assert.HasCount(1, cut.FindAll("[data-testid=agent-overview] .labels-summary"));
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        var panel = cut.Find("[data-testid=agent-identity-section]");
        Assert.AreEqual(identity, panel.QuerySelector("h2")!.TextContent);
        Assert.IsNotNull(panel.QuerySelector("[data-testid=labels-editor]"));
        Assert.Contains(labels, panel.TextContent);
        Assert.IsEmpty(cut.FindAll("[data-testid=agent-labels-section], [data-testid=agent-behavior-section]"));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        var labelEditor = cut.FindComponent<LabelsEditor>().Instance;
        cut.Find("[data-testid=label-add]").Click();
        cut.Find("[data-testid=label-key]").Input("environment");
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        Assert.IsTrue(panel.ParentElement!.ParentElement!.HasAttribute("hidden"));
        await cut.Find("#agent-resource-form").SubmitAsync();
        Assert.AreEqual("definition/identity", navigation.CurrentPath);
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreSame(labelEditor, cut.FindComponent<LabelsEditor>().Instance);
        Assert.AreEqual("environment", cut.Find("[data-testid=label-key]").GetAttribute("value"));
        Assert.IsNull(handler.LastRequest);
        Assert.IsNotNull(cut.Find(".label-row-error"));
        cut.Find("[data-testid=label-value]").Input("production");
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.Contains(emptyLabels, cut.Find("[data-testid=agent-identity-summary] .labels-summary").TextContent);
        Assert.DoesNotContain("production", cut.Find("[data-testid=agent-identity-summary]").TextContent);
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        await cut.Find("[data-testid=agent-refresh]").ClickAsync();
        Assert.AreSame(labelEditor, cut.FindComponent<LabelsEditor>().Instance);
        Assert.AreEqual("production", cut.Find("[data-testid=label-value]").GetAttribute("value"));
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=agent-save-draft]").Click();
        await cut.Find("#agent-resource-form").SubmitAsync();
        Assert.AreEqual("production", handler.LastRequest!.Metadata.Tags["environment"]);
        Assert.AreEqual("welcome", handler.LastRequest.Metadata.Name);
        Assert.AreEqual(ResourceScopeRef.Instance, handler.LastRequest.ScopeRef);
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.AreEqual("environment", cut.Find("[data-testid=agent-identity-summary] .resource-label strong").TextContent);
        Assert.Contains("production", cut.Find("[data-testid=agent-identity-summary] .labels-summary").TextContent);
        Assert.HasCount(1, cut.FindAll("[data-testid=agent-overview] .labels-summary"));
        Assert.AreEqual(labels, cut.Find("[data-testid=agent-identity-summary] .labels-summary").ParentElement!.PreviousElementSibling!.TextContent);
    }

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
        var sections = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionState;
        Assert.AreSame(sections, cut.FindComponent<ResourceDetailsTemplate>().Instance.SectionState);
        Assert.AreEqual(identity, cut.Find("[data-testid=agent-identity-summary] h2").TextContent);
        Assert.HasCount(3, cut.FindComponents<MetricCard>());
        Assert.IsEmpty(cut.FindComponents<StateComparison>());
        Assert.AreEqual("1", cut.FindComponents<MetricCard>()[0].Instance.Value);
        Assert.AreEqual(locale == "fr-FR" ? "Génération souhaitée" : "Desired generation", cut.FindComponents<MetricCard>()[0].Instance.Label);
        Assert.AreEqual(locale == "fr-FR" ? "Aucune" : "None", cut.FindComponents<MetricCard>()[1].Instance.Value);
        Assert.AreEqual(locale == "fr-FR" ? "Non déployée" : "Not deployed", cut.FindComponents<MetricCard>()[2].Instance.Value);
        Assert.AreEqual("resource-overview-metrics", cut.Find("[data-testid=agent-overview]").FirstElementChild!.ClassName);
        Assert.AreEqual(valueHeader, cut.Find("[data-testid=agent-configuration-summary] th:last-child").TextContent);
        Assert.Contains(handler.Current.Definition.Instructions, cut.Find("[data-testid=agent-configuration-summary]").TextContent);
        Assert.Contains("scopeRef=%2Finstance", cut.Find("[data-testid=agent-configuration-summary] a[href*='/modelprofiles/']").GetAttribute("href")!);
        Assert.IsEmpty(cut.FindAll("[role=switch]"), "Agent has no enablement property.");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        Assert.AreEqual("definition/identity", sections.CurrentPath);
        Assert.HasCount(1, cut.FindAll(".resource-details-guidance .required-fields-note"));
        Assert.IsEmpty(cut.FindAll(".resource-header .required-fields-note"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/model-execution"));
        Assert.HasCount(1, cut.FindAll(".resource-details-guidance .required-fields-note"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/contracts"));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll(".required-fields-note")));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        Assert.IsEmpty(cut.FindAll("form .required-fields-note"));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        cut.Find("[data-testid=agent-display-name]").Input("Unsaved agent name");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));
        Assert.AreEqual("overview", sections.CurrentPath);
        Assert.Contains("Saved agent", cut.Find("[data-testid=agent-identity-summary]").TextContent);
        Assert.DoesNotContain("Unsaved agent name", cut.Find("[data-testid=agent-identity-summary]").TextContent);
        Assert.IsNotNull(cut.Find(".resource-details-main > .unsaved-changes-bar"));
        Assert.AreEqual("agent-resource-form", cut.Find("[data-testid=agent-save-draft]").GetAttribute("form"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
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
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=agent-instructions]").Change(string.Empty);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));
        await cut.Find("#agent-resource-form").SubmitAsync();
        Assert.AreEqual("page", cut.Find("a[data-resource-section='definition/configuration']").GetAttribute("aria-current"));
        Assert.HasCount(1, cut.FindAll(".validation-message"));
        Assert.AreEqual(locale == "fr-FR" ? "Le champ Instructions est obligatoire." : "Instructions is required.", cut.Find(".validation-message").TextContent);
        Assert.AreEqual("Unsaved agent name", handler.Current.Definition.DisplayName);
        cut.Find("[data-testid=agent-discard]").Click();
        Assert.AreEqual(handler.Current.Definition.Instructions, cut.Find("[data-testid=agent-instructions]").GetAttribute("value"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/tools"));
        Assert.IsEmpty(cut.FindAll(".required-fields-note"));
    }

    [TestMethod]
    [DataRow("en-US", "Display name is required.", "Technical name has an invalid format.")]
    [DataRow("fr-FR", "Le champ Nom d’affichage est obligatoire.", "Le format du champ Nom technique n’est pas valide.")]
    public async Task IdentityValidationUsesTheDisplayedLabelsUnderTheCorrectControl(string locale, string required, string format)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/agents/new");
        var cut = context.Render<AgentEditor>();
        cut.Find("[data-testid=agent-display-name]").Input("Valid name");
        cut.Find("[data-testid=agent-display-name]").Input(string.Empty);
        var field = cut.Find("[data-testid=agent-display-name]").Closest(".form-field")!;
        Assert.AreEqual(required, field.QuerySelector(".validation-message")!.TextContent);
        Assert.AreEqual(field.QuerySelector(".form-field-messages")!.Id, field.QuerySelector("input")!.GetAttribute("aria-describedby"));
        cut.Find("[data-testid=agent-name]").Input("bad name!");
        Assert.AreEqual(format, cut.Find("[data-testid=agent-name]").Closest(".form-field")!.QuerySelector(".validation-message")!.TextContent);
        Assert.IsNull(handler.LastRequest);
        await cut.Find("[data-testid=agent-display-name]").InputAsync("Restored name");
        Assert.IsNull(cut.Find("[data-testid=agent-display-name]").Closest(".form-field")!.QuerySelector(".validation-message"));
    }

    [TestMethod]
    public async Task LayoutYamlActionAppliesToTheValidatedFamilyDraftWithoutSaving()
    {
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        Assert.IsEmpty(cut.FindAll(".required-fields-note, [data-testid=agent-yaml-editor] .form-actions"));
        var yaml = cut.Find("[data-testid=agent-yaml-editor] textarea").GetAttribute("value")!;
        cut.Find("[data-testid=agent-yaml-editor] textarea").Input(yaml.Replace("Saved agent", "Applied from section action", StringComparison.Ordinal));
        await cut.Find(".resource-details-main > .resource-section-actions button").ClickAsync();
        Assert.IsNull(handler.LastRequest);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        Assert.IsEmpty(cut.FindAll(".resource-section-actions"));
        Assert.AreEqual("Applied from section action", cut.Find("[data-testid=agent-display-name]").GetAttribute("value"));
        Assert.IsNotNull(cut.Find("[data-testid=agent-save-draft]"));
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
            await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
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
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
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
        Assert.IsNotNull(cut.Find("[data-testid=agent-resource-details] .resource-header"));
        Assert.IsNotNull(cut.Find(".resource-details-state [role=status]"));
        Assert.IsEmpty(cut.FindAll("#agent-resource-form, .resource-details-navigation, [data-testid=agent-refresh]"));
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
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/agents/welcome?context=%2Finstance#definition/identity");
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
    [DataRow("en-US", "Unable to load resource", "Retry")]
    [DataRow("fr-FR", "Impossible de charger la ressource", "Réessayer")]
    public async Task InitialLoadFailureUsesTheTemplateAndRetryLoadsTheResource(string locale, string title, string retry)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new AgentHandler { AgentFailure = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        cut.WaitForAssertion(() => Assert.AreEqual(title, cut.Find(".resource-details-state h2").TextContent));
        Assert.AreEqual("welcome", cut.Find(".resource-header h1").TextContent);
        Assert.IsEmpty(cut.FindAll("#agent-resource-form, .resource-details-navigation, .resource-owner-scope"));
        Assert.AreEqual(retry, cut.Find("[data-testid=agent-retry]").TextContent);
        handler.AgentFailure = null;
        await cut.Find("[data-testid=agent-retry]").ClickAsync();
        cut.WaitForAssertion(() => Assert.AreEqual("Saved agent", cut.Find(".resource-header h1").TextContent));
        Assert.IsEmpty(cut.FindAll(".resource-details-state"));
        Assert.IsNotNull(cut.Find("[data-testid=agent-refresh]"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshAndRetryKeepFormYamlAndPolicyDraftsMounted(bool yamlDraft)
    {
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("access"));
        var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(p => p.DraftChanged).InvokeAsync(true));
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        cut.Find("[data-testid=agent-display-name]").Input("Preserved form draft");
        if (yamlDraft)
        {
            await cut.InvokeAsync(() => navigation.NavigateSection("yaml"));
            cut.Find("[data-testid=agent-yaml-editor] textarea").Input("invalid: [preserved YAML");
        }
        else
        {
            await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
            cut.Find("[data-testid=agent-instructions]").Input(string.Empty);
        }
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        var editContext = form.EditContext;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.AgentReadGate = gate;
        var pending = cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.Refreshing, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status));
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.IsNotNull(cut.Find(".resource-refresh-status"));
        Assert.IsTrue(cut.FindAll(".unsaved-changes-bar button").All(button => button.HasAttribute("disabled")));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        gate.SetResult();
        await pending;
        handler.AgentFailure = HttpStatusCode.ServiceUnavailable;
        await cut.Find("[data-testid=agent-refresh]").ClickAsync();
        Assert.IsNotNull(cut.Find(".resource-refresh-error"));
        Assert.AreSame(editContext, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance.EditContext);
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.AreEqual("Preserved form draft", cut.Find("[data-testid=agent-display-name]").GetAttribute("value"));
        if (yamlDraft) Assert.AreEqual("invalid: [preserved YAML", cut.Find("[data-testid=agent-yaml-editor] textarea").GetAttribute("value"));
        else Assert.HasCount(1, cut.FindAll(".validation-message"));
        handler.AgentFailure = null;
        await cut.Find("[data-testid=agent-retry]").ClickAsync();
        Assert.IsEmpty(cut.FindAll(".resource-refresh-error"));
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.IsNull(handler.LastRequest);
    }

    [TestMethod]
    public async Task RefreshResponseFromThePreviousContextCannotOverwriteTheNewDraft()
    {
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.AgentReadGate = gate;
        var pending = cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find(".resource-refresh-status")));
        handler.Current = handler.Current with { Definition = handler.Current.Definition with { DisplayName = "New context snapshot" } };
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/agents/welcome?context=%2Finstance#definition/identity");
        cut.Render(p => p.Add(c => c.Name, "welcome"));
        cut.WaitForAssertion(() => Assert.AreEqual("New context snapshot", cut.Find(".resource-header h1").TextContent));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("[data-testid=agent-display-name]")));
        cut.Find("[data-testid=agent-display-name]").Input("New context draft");
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        gate.SetResult();
        await pending;
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreEqual("New context draft", cut.Find("[data-testid=agent-display-name]").GetAttribute("value"));
        Assert.AreEqual("New context snapshot", cut.Find(".resource-header h1").TextContent);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task RefreshWithoutADraftAdoptsTheServerSnapshotAndRetainsTheSection()
    {
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        handler.Current = handler.Current with { Generation = 2, Definition = handler.Current.Definition with { DisplayName = "Refreshed name" } };
        handler.CurrentEtag = "\"refreshed\"";
        await cut.Find("[data-testid=agent-refresh]").ClickAsync();
        Assert.AreEqual("Refreshed name", cut.Find(".resource-header h1").TextContent);
        Assert.AreEqual("yaml", cut.FindComponent<ResourceNavigation>().Instance.CurrentPath);
        Assert.Contains("Refreshed name", cut.Find("[data-testid=agent-yaml-editor] textarea").GetAttribute("value")!);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task RefreshKeepsTheDraftEtagUntilDiscardExplicitlyAdoptsTheNewSnapshot()
    {
        using var handler = new AgentHandler { EnforceEtag = true };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        cut.Find("[data-testid=agent-display-name]").Input("Local draft");
        handler.Current = handler.Current with { Generation = 2, Definition = handler.Current.Definition with { DisplayName = "Remote change" } };
        handler.CurrentEtag = "\"remote\"";
        await cut.Find("[data-testid=agent-refresh]").ClickAsync();
        Assert.AreEqual("Local draft", cut.Find("[data-testid=agent-display-name]").GetAttribute("value"));
        Assert.AreEqual("Saved agent", cut.Find(".resource-header h1").TextContent);
        cut.Find("[data-testid=agent-save-draft]").Click();
        await cut.Find("#agent-resource-form").SubmitAsync();
        Assert.AreEqual("\"initial\"", handler.LastEtag);
        Assert.AreEqual("Remote change", handler.Current.Definition.DisplayName);
        Assert.AreEqual("Local draft", cut.Find("[data-testid=agent-display-name]").GetAttribute("value"));
        await cut.Find("[data-testid=agent-discard]").ClickAsync();
        Assert.AreEqual("Remote change", cut.Find(".resource-header h1").TextContent);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, ResourceLoadStatus.Forbidden)]
    [DataRow(HttpStatusCode.NotFound, ResourceLoadStatus.NotFound)]
    [DataRow(HttpStatusCode.OK, ResourceLoadStatus.NotFound)]
    public async Task RefreshClearsCachedContentWhenPermissionIdentityOrAvailabilityChanges(HttpStatusCode status, ResourceLoadStatus expected)
    {
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        cut.Find("[data-testid=agent-display-name]").Input("Private draft");
        if (status == HttpStatusCode.OK) handler.Current = handler.Current with { Uid = Guid.NewGuid() };
        else handler.AgentFailure = status;
        await cut.Find("[data-testid=agent-refresh]").ClickAsync();
        Assert.AreEqual(expected, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.IsEmpty(cut.FindAll("#agent-resource-form, .resource-details-navigation, .unsaved-changes-bar, [data-testid=agent-refresh], [data-testid=agent-retry]"));
        Assert.DoesNotContain("Private draft", cut.Markup);
        Assert.DoesNotContain("Saved agent", cut.Markup);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task InheritedDefinitionStaysReadOnlyAndHasNoDraftActions()
    {
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http, ResourceScopeRef.Workspace(Guid.NewGuid()));
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        Assert.IsTrue(cut.Find("[data-testid=agent-definition-fields]").HasAttribute("disabled"));
        Assert.HasCount(1, cut.FindAll("[data-testid=inherited-resource-notice]"));
        Assert.Contains("context=%2Finstance", cut.Find("[data-testid=switch-to-owner-context]").GetAttribute("href")!);
        await cut.Find("#agent-resource-form").SubmitAsync();
        Assert.IsNull(handler.LastRequest);
        Assert.IsEmpty(cut.FindAll(".required-fields-note"));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar, [data-resource-section=access]"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        Assert.IsTrue(cut.Find("[data-testid=agent-yaml-editor] textarea").HasAttribute("readonly"));
        Assert.IsEmpty(cut.FindAll(".resource-section-actions"));
        Assert.IsNull(handler.LastRequest);
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task CreationWizardRetainsFieldsAndValidatesBeforeCreatingAndPreparing(string locale)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/agents/new");
        var cut = context.Render<AgentEditor>();
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        Assert.IsNull(handler.LastRequest);
        cut.Find("[data-testid=agent-display-name]").Input("New agent");
        cut.Find("[data-testid=agent-name]").Input("new-agent");
        await cut.InvokeAsync(() => cut.FindComponent<Stub<ResourceScopeFormField>>().Instance.Parameters.Get(p => p.ValueChanged).InvokeAsync(ResourceScopeRef.Instance));
        cut.Find("[data-testid=label-add]").Click();
        cut.Find("[data-testid=label-key]").Input("environment");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        Assert.IsNull(handler.LastRequest);
        cut.Find("[data-testid=label-value]").Input("local");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("configuration", wizard.CurrentStepId);
        Assert.IsNull(handler.LastRequest);
        cut.Find("[data-testid=agent-instructions]").Change("Saved instructions");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("tools"));
        Assert.IsNotNull(cut.Find("[data-testid=agent-tool-configuration]"));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("model-execution", wizard.CurrentStepId);
        await cut.InvokeAsync(() => cut.FindComponent<Stub<ModelProfilePicker>>().Instance.Parameters.Get(p => p.ValueChanged).InvokeAsync("reasoning-default"));
        cut.Find("[data-testid=agent-runtime-profile]").Change("/instance|default|runtime");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("identity"));
        Assert.AreEqual("New agent", cut.Find("[data-testid=agent-display-name]").GetAttribute("value"));
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.IsNotNull(handler.LastRequest);
        Assert.AreEqual("new-agent", handler.LastRequest.Metadata.Name);
        Assert.AreEqual("local", handler.LastRequest.Metadata.Tags["environment"]);
        Assert.AreEqual(ResourceScopeRef.Instance, handler.LastRequest.ScopeRef);
        Assert.AreEqual("Saved instructions", handler.LastRequest.Definition.Instructions);
        Assert.AreEqual(1, handler.Preparations);
        Assert.IsTrue(handler.CreateOnly);
        Assert.Contains("/agents/new-agent", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [TestMethod]
    public async Task AccessDraftKeepsItsPanelAndUidAcrossSectionsAndDefinitionGenerationChanges()
    {
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        var nav = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => nav.NavigateSection("access"));
        var access = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        Assert.AreEqual(handler.Current.Uid, access.Parameters.Get(p => p.ResourceUid));
        await cut.InvokeAsync(() => access.Parameters.Get(p => p.DraftChanged).InvokeAsync(true));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        await cut.InvokeAsync(() => nav.NavigateSection("definition/identity"));
        cut.Find("[data-testid=agent-display-name]").Input("Definition saved independently");
        cut.Find("[data-testid=agent-save-draft]").Click();
        await cut.Find("#agent-resource-form").SubmitAsync();
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await cut.InvokeAsync(() => nav.NavigateSection("access"));
        Assert.AreSame(access, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.AreEqual(handler.Current.Generation, access.Parameters.Get(p => p.Generation));
        await cut.InvokeAsync(() => access.Parameters.Get(p => p.DraftChanged).InvokeAsync(false));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task DeploymentMetricsCompareSavedAndActiveGenerationsWithoutApplyingChanges()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new AgentHandler { ReadyGeneration = 1 };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        cut.WaitForAssertion(() => Assert.AreEqual("Ready", cut.FindComponents<MetricCard>()[2].Instance.Value));
        Assert.AreEqual(UiStatus.Success, cut.FindComponents<MetricCard>()[2].Instance.Status);
        Assert.AreEqual("1", cut.FindComponents<MetricCard>()[1].Instance.Value);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        cut.Find("[data-testid=agent-display-name]").Input("Changed definition");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));
        Assert.AreEqual("Changes to apply", cut.FindComponents<MetricCard>()[2].Instance.Value);
        Assert.AreEqual("1", cut.FindComponents<MetricCard>()[0].Instance.Value);
        Assert.AreEqual("1", cut.FindComponents<MetricCard>()[1].Instance.Value);
        Assert.AreEqual("Saved agent", cut.FindComponent<ResourceIdentitySummary>().Instance.DisplayName);
        Assert.IsNull(handler.LastRequest);
        cut.Find("[data-testid=agent-save-draft]").Click();
        await cut.Find("#agent-resource-form").SubmitAsync();
        Assert.AreEqual("2", cut.FindComponents<MetricCard>()[0].Instance.Value);
        Assert.AreEqual("1", cut.FindComponents<MetricCard>()[1].Instance.Value);
        Assert.AreEqual("Changes to apply", cut.FindComponents<MetricCard>()[2].Instance.Value);
        Assert.AreEqual(0, handler.Preparations);
    }

    [TestMethod]
    [DataRow("en-US", "Failed", "Error", UiStatus.Danger)]
    [DataRow("fr-FR", "Failed", "En erreur", UiStatus.Danger)]
    [DataRow("en-US", "agent_version_not_found", "Not deployed", UiStatus.Neutral)]
    [DataRow("fr-FR", "agent_deployment_not_found", "Non déployée", UiStatus.Neutral)]
    public void DeploymentKeepsACompactLocalizedStatusAndDistinguishesMissingFromFailed(string locale, string state, string status, UiStatus tone)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new AgentHandler { ReadinessError = "Runtime unavailable", ReadinessState = state };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        cut.WaitForAssertion(() => Assert.HasCount(3, cut.FindComponents<MetricCard>()));
        var deployment = cut.FindComponents<MetricCard>()[2].Instance;
        Assert.AreEqual(status, deployment.Value);
        Assert.IsFalse(string.IsNullOrWhiteSpace(deployment.Detail));
        if (tone == UiStatus.Danger) Assert.AreEqual("Runtime unavailable", deployment.Detail);
        Assert.AreEqual(tone, deployment.Status);
        Assert.IsNull(handler.LastRequest);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task YamlIncludesTheExactOwnerAndRejectsChangingIt(bool changeOwner)
    {
        using var handler = new AgentHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = Context(http);
        var cut = context.Render<AgentEditor>(p => p.Add(c => c.Name, "welcome"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        var yaml = cut.Find("[data-testid=agent-yaml-editor] textarea").GetAttribute("value")!;
        Assert.Contains("scopeRef: /instance", yaml);
        yaml = changeOwner ? yaml.Replace("scopeRef: /instance", "scopeRef: /tenant/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", StringComparison.Ordinal) : yaml.Replace("Saved agent", "Saved from YAML", StringComparison.Ordinal);
        cut.Find("[data-testid=agent-yaml-editor] textarea").Input(yaml);
        await cut.Find(".unsaved-changes-bar button.button-primary").ClickAsync();
        if (changeOwner)
        {
            Assert.IsNull(handler.LastRequest);
            Assert.IsNotNull(cut.Find("[data-testid=agent-yaml-editor] [role=alert]"));
            Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        }
        else
        {
            Assert.IsNotNull(handler.LastRequest);
            Assert.AreEqual(ResourceScopeRef.Instance, handler.LastRequest.ScopeRef);
            Assert.AreEqual("Saved from YAML", handler.Current.Definition.DisplayName);
            Assert.AreEqual("\"initial\"", handler.LastEtag);
        }
    }

    private static BunitContext Context(HttpClient http, ResourceScopeRef? scope = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/identity", "definition/configuration", "definition/model-execution", "definition/tools", "definition/contracts", "yaml", "access" })
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
        context.ComponentFactories.AddStub<ResourceAccessPanel>();
        context.ComponentFactories.AddStub<ReusableSchemaBindingEditor>();
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
        public HttpStatusCode? AgentFailure { get; set; }
        public TaskCompletionSource? AgentReadGate { get; set; }
        public string CurrentEtag { get; set; } = "\"initial\"";
        public bool EnforceEtag { get; init; }
        public long? ReadyGeneration { get; init; }
        public string? ReadinessError { get; init; }
        public string? ReadinessState { get; init; }
        public TaskCompletionSource? ModelLookupGate { get; init; }
        private int modelLookups;
        public int Preparations { get; private set; }
        public bool CreateOnly { get; private set; }
        public AgentResource Current { get; set; } = new()
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
            if (path == "/api/runtime/agents/welcome/readiness" && (ReadyGeneration is not null || ReadinessError is not null))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new Agentstration.Runtime.Contracts.AgentRuntimeReadinessResponse("welcome", ReadyGeneration ?? 1, ReadinessError is null, ReadinessState ?? (ReadinessError is null ? "Ready" : "Failed"), "deployment", null, ReadinessError, "maf-builtin")) };
            if (path.EndsWith("/model", StringComparison.Ordinal) && Interlocked.Increment(ref modelLookups) == 1 && ModelLookupGate is not null)
                await ModelLookupGate.Task.WaitAsync(cancellationToken);
            if (path == "/api/runtime/agents/new-agent/prepare")
            {
                Preparations++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new Agentstration.Runtime.Contracts.PrepareAgentRuntimeResponse("new-agent", Current.Generation, "deployment", "revision", "Ready")) };
            }
            if (path is "/api/agents/welcome" or "/api/agents/new-agent")
            {
                if (request.Method == HttpMethod.Get)
                {
                    var value = Current;
                    var currentEtag = CurrentEtag;
                    var gate = AgentReadGate; AgentReadGate = null;
                    if (gate is not null) await gate.Task.WaitAsync(cancellationToken);
                    if (AgentFailure is { } failure) return new(failure) { Content = JsonContent.Create(new { title = "Transport failure", status = (int)failure }) };
                    var read = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
                    read.Headers.ETag = new(currentEtag);
                    return read;
                }
                if (request.Method == HttpMethod.Put)
                {
                    LastEtag = request.Headers.IfMatch.SingleOrDefault()?.ToString();
                    if (EnforceEtag && LastEtag != CurrentEtag) return new(HttpStatusCode.PreconditionFailed) { Content = JsonContent.Create(new { title = "Resource version conflict", status = 412 }) };
                    CreateOnly = request.Headers.IfNoneMatch.Any(value => value.Tag == "*");
                    LastRequest = (await request.Content!.ReadFromJsonAsync<AgentResourceRequest>(cancellationToken))!;
                    Current = Current with { Metadata = LastRequest.Metadata, ScopeRef = LastRequest.ScopeRef, Definition = LastRequest.Definition, Generation = Current.Generation + 1 };
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
