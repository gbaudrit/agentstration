using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolProviderEditorTests
{
    [TestMethod]
    public async Task ProviderNavigationKeepsOnlyTheSelectedSectionVisibleAndRetainsTheEditor()
    {
        using var context = Context(new ProviderClient(), false);
        var rendered = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        var navigation = rendered.FindComponent<ResourceNavigation>().Instance;
        Assert.IsNotNull(rendered.Find("[data-testid=provider-identity-summary]"));
        Assert.IsEmpty(rendered.FindAll(".resource-form"));
        Assert.IsEmpty(rendered.FindAll("[data-testid=provider-tools-panel]"));
        await rendered.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        var form = rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        await rendered.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        Assert.HasCount(1, rendered.FindAll(".resource-details-guidance .required-fields-note"));
        Assert.IsEmpty(rendered.FindAll(".resource-header .required-fields-note"));
        Assert.IsNull(rendered.Find("[data-testid=provider-connection-panel]").Closest("[hidden]"));
        await rendered.InvokeAsync(() => navigation.NavigateSection("tools"));
        Assert.IsEmpty(rendered.FindAll(".required-fields-note"));
        Assert.IsNull(rendered.Find("[data-testid=provider-tools-panel]").Closest("[hidden]"));
        Assert.IsNotNull(rendered.Find("[data-testid=provider-connection-panel]").Closest("[hidden]"));
        await rendered.InvokeAsync(() => navigation.NavigateSection("definition/identity"));
        Assert.AreSame(form, rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
    }

    [TestMethod]
    [DataRow("en-US", "Identity", "Yes")]
    [DataRow("fr-FR", "Identité", "Oui")]
    public async Task ProviderHeaderSwitchIsADraftAndSaveKeepsTheExistingEtag(string locale, string identity, string yes)
    {
        using var culture = new TestCultureScope(locale);
        var client = new ProviderClient();
        using var context = Context(client, false);
        var cut = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        Assert.Contains(identity, cut.Find(".resource-identity-summary").TextContent);
        Assert.Contains(yes, cut.Find(".resource-configuration-summary").TextContent);
        cut.Find(".resource-header input[role=switch]").Change(false);
        Assert.AreEqual(0, client.Writes);
        Assert.IsTrue(client.Provider.Definition.Enabled);
        Assert.HasCount(1, cut.FindAll(".resource-details-main > .unsaved-changes-bar"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.IsFalse(cut.Find(".resource-header input[role=switch]").HasAttribute("checked"));
        await cut.Find("#tool-provider-resource-form").SubmitAsync();
        Assert.AreEqual(1, client.Writes);
        Assert.AreEqual("\"initial\"", client.LastEtag);
        Assert.IsFalse(client.Provider.Definition.Enabled);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        cut.Find(".resource-header input[role=switch]").Change(true);
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.IsFalse(cut.Find(".resource-header input[role=switch]").HasAttribute("checked"));
        Assert.AreEqual(1, client.Writes);
    }

    [TestMethod]
    public async Task ProviderWizardAllowsIncompleteNavigationButDoesNotCreateAnIncompleteConnection()
    {
        var client = new ProviderClient();
        using var context = Context(client, true);
        var cut = context.Render<ToolProviderEditor>();
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        Assert.AreEqual(0, client.Writes);
        var identity = cut.FindComponent<ResourceIdentityFields>().Instance;
        await cut.InvokeAsync(() => identity.DisplayNameChanged.InvokeAsync("My provider"));
        await cut.InvokeAsync(() => identity.TechnicalNameChanged.InvokeAsync("my-provider"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceScopeFormField>().Instance.ValueChanged.InvokeAsync(ResourceScopeRef.Instance));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("connection", wizard.CurrentStepId);
        Assert.AreEqual(0, client.Writes);
        cut.Find("input[placeholder=npx]").Change("my-command");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual(1, client.Writes);
        Assert.Contains("/tools/providers/my-provider", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void ClearingConnectionCommandDoesNotChangeItsTransportOrInvokeProviderCommands(string locale)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(new ProviderClient());
        var editor = context.Render<ToolProviderEditor>();
        var transport = editor.WaitForElement("#connection select").GetAttribute("value");
        editor.Find("input[placeholder='npx']").Input("my-command");
        var command = editor.Find("input[placeholder='npx']");
        var field = command.Closest(".form-field")!;
        Assert.AreEqual(command.Id, field.QuerySelector("label")!.GetAttribute("for"));
        field.QuerySelector(".field-clear")!.Click();

        Assert.AreEqual(string.Empty, editor.Find("input[placeholder='npx']").GetAttribute("value"));
        Assert.AreEqual(transport, editor.Find("#connection select").GetAttribute("value"));
        Assert.IsEmpty(editor.FindAll(".form-alert-danger"));
    }

    [TestMethod]
    public async Task ActivationDraftCanBeSavedBeforeVisitingDefinition()
    {
        var client = new ProviderClient(); using var context = Context(client, false);
        var cut = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        Assert.IsEmpty(cut.FindAll(".resource-form"));
        cut.Find(".resource-header input[role=switch]").Change(false);
        Assert.HasCount(1, cut.FindAll("#tool-provider-resource-form"));
        await cut.Find("#tool-provider-resource-form").SubmitAsync();
        Assert.AreEqual(1, client.Writes); Assert.IsFalse(client.Provider.Definition.Enabled);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task InvalidConnectionAndOriginalContextSurviveRefreshUntilExplicitDiscard()
    {
        var client = new ProviderClient(); using var context = Context(client, false);
        var cut = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        var original = cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        cut.Find("input[placeholder='https://server.example/mcp']").Input("invalid endpoint");
        await cut.Find("#tool-provider-resource-form").SubmitAsync();
        Assert.HasCount(1, cut.FindAll(".validation-message"));
        client.Provider = client.Provider with { Generation = 2, Definition = client.Provider.Definition with { DisplayName = "New server name" } }; client.ReadEtag = "\"newer\"";
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(original, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("invalid endpoint", cut.Find("input[placeholder='https://server.example/mcp']").GetAttribute("value"));
        Assert.AreEqual("\"initial\"", cut.Find("#tool-provider-resource-form").GetAttribute("data-resource-etag"));
        Assert.HasCount(1, cut.FindAll(".validation-message"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        Assert.IsNotNull(cut.Find(".resource-yaml-editor textarea[readonly]"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.AreNotSame(original, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("\"newer\"", cut.Find("#tool-provider-resource-form").GetAttribute("data-resource-etag"));
        Assert.IsEmpty(cut.FindAll(".validation-message")); Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task ChangesMadeWhileRefreshIsInFlightKeepTheirContextAndEtag()
    {
        var client = new ProviderClient(); using var context = Context(client, false);
        var cut = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        var original = cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        client.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        await cut.InvokeAsync(() => cut.FindComponent<ResourceIdentityEditor>().Instance.DisplayNameChanged.InvokeAsync("Changed during refresh"));
        client.PendingRead.SetResult(new(client.Provider with { Generation = 2 }, "\"newer\"")); await refresh;
        Assert.AreSame(original, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("Changed during refresh", ((ToolProviderEditorModel)original.Model).DisplayName);
        Assert.AreEqual("\"initial\"", cut.Find("#tool-provider-resource-form").GetAttribute("data-resource-etag"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostAccessOrReplacementClearsDefinitionYamlAndPolicyDrafts(bool replace)
    {
        var client = new ProviderClient(); using var context = Context(client, false); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceIdentityEditor>().Instance.DisplayNameChanged.InvokeAsync("Private pending content"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        await cut.InvokeAsync(() => cut.FindComponent<Stub<ResourceAccessPanel>>().Instance.Parameters.Get(x => x.DraftChanged).InvokeAsync(true));
        if (replace) client.Provider = client.Provider with { Uid = Guid.NewGuid() };
        else client.ReadError = new AgentstrationApiException("Forbidden", "fixture", System.Net.HttpStatusCode.Forbidden);
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.IsEmpty(cut.FindAll(".resource-form")); Assert.IsEmpty(cut.FindAll(".resource-yaml-editor"));
        Assert.DoesNotContain("Private pending content", cut.Markup);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public void AepModelPreservesExactAndRelativeExtensionCoordinates()
    {
        foreach (var reference in new[] { new ResourceReference("same", ResourceScopeRef.Instance, new ResourceNamespace("shared")), new ResourceReference("same") })
        {
            var source = new ProviderClient().Provider;
            var model = ToolProviderEditorModel.From(source with { Definition = source.Definition with { ProviderType = ToolProviderType.Aep, Mcp = null, Aep = new() { Extension = reference } } });
            model.DisplayName = "Edited";
            Assert.AreEqual(reference, model.Properties().Aep!.Extension);
        }
    }

    [TestMethod]
    public async Task ManagedDefinitionIsLockedWhileIndependentAccessRemainsAvailable()
    {
        var client = new ProviderClient(); client.Provider = client.Provider with { Governance = ResourceGovernance.Core("fixture") };
        using var context = Context(client, false); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        Assert.IsTrue(cut.Find(".resource-header input[role=switch]").HasAttribute("disabled"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        Assert.IsTrue(cut.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        var access = cut.FindComponent<Stub<ResourceAccessPanel>>();
        Assert.AreEqual(client.Provider.Uid, access.Instance.Parameters.Get(x => x.ResourceUid));
        await cut.InvokeAsync(() => access.Instance.Parameters.Get(x => x.DraftChanged).InvokeAsync(true));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.AreEqual(0, client.Writes); Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task InvalidIdentityRoutesBackFromConfigurationAndKeepsTheSameModel()
    {
        var client = new ProviderClient(); using var context = Context(client, false);
        var cut = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        await cut.InvokeAsync(() => cut.FindComponent<ResourceIdentityEditor>().Instance.DisplayNameChanged.InvokeAsync(string.Empty));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        await cut.Find("#tool-provider-resource-form").SubmitAsync();
        Assert.AreEqual("definition/identity", cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionState.CurrentPath);
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.HasCount(1, cut.FindAll("[data-definition-section='definition/identity'] .validation-message"));
        Assert.AreEqual(0, client.Writes);
    }

    [TestMethod]
    public async Task EmbeddedToolsUseTheSameLazyExactPreviewWithoutMutatingProviderOrTool()
    {
        var client = new ProviderClient { IncludeTool = true };
        using var context = Context(client, false, true);
        var cut = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("tools"));
        Assert.AreEqual(0, client.ToolReads);
        await cut.Find("[data-testid=provider-tool-preview]").ClickAsync(new());
        Assert.HasCount(1, cut.FindComponents<ToolResourceSummary>());
        Assert.AreEqual(1, client.ToolReads);
        Assert.AreEqual(0, client.Writes);
        Assert.IsEmpty(cut.FindAll("[data-testid=tool-preview-edit]"));
    }

    private static BunitContext Context(ProviderClient client, bool create, bool inspectTools = false)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(client);
        if (inspectTools) context.WithResourceListContext();
        var uri = create ? "http://localhost/tools/providers/new" : "http://localhost/tools/providers/microsoft-learn?scopeRef=%2Finstance";
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/identity", "definition/configuration", "tools", "access", "yaml" })
        {
            var target = uri + "#" + section;
            module.Setup<string>("navigate", i => i.Arguments[0] is string value && value.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(target);
        }
        return context;
    }

    private sealed class ProviderClient : IToolsClient
    {
        public ToolProviderResource Provider { get; set; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new ResourceMetadata { Name = "microsoft-learn" },
            Uid = Guid.NewGuid(),
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            Definition = new ToolProviderProperties
            {
                DisplayName = "Microsoft Learn",
                ProviderType = ToolProviderType.Mcp,
                Mcp = new McpToolProviderConfiguration
                {
                    Transport = McpToolProviderTransport.StreamableHttp,
                    Endpoint = new Uri("https://learn.microsoft.com/api/mcp")
                }
            }
        };

        public string ReadEtag { get; set; } = "\"initial\"";
        public Exception? ReadError { get; set; }
        public TaskCompletionSource<ResourceSnapshot<ToolProviderResource>>? PendingRead { get; set; }
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) =>
            ReadError is { } error ? Task.FromException<ResourceSnapshot<ToolProviderResource>>(error) : PendingRead?.Task ?? Task.FromResult(new ResourceSnapshot<ToolProviderResource>(Provider, ReadEtag));
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
        {
            Assert.AreEqual(Provider.ScopeRef, scopeRef); Assert.AreEqual(Provider.Namespace, @namespace); Assert.AreEqual(Provider.Name, name);
            return GetProviderAsync(name, cancellationToken);
        }
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolResource>>(IncludeTool ? [Tool] : []);
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public int Writes { get; private set; }
        public string? LastEtag { get; private set; }
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken)
        {
            Writes++; Provider = Provider with { Metadata = new() { Name = request.Name }, ScopeRef = request.ScopeRef, Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<ToolProviderResource>(Provider, "\"created\""));
        }
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken)
        {
            Writes++; LastEtag = etag; Provider = Provider with { Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<ToolProviderResource>(Provider, "\"updated\""));
        }
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool IncludeTool { get; set; }
        public int ToolReads { get; private set; }
        private ToolResource Tool => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new() { Name = "saved-tool" },
            ScopeRef = Provider.ScopeRef,
            Definition = new() { DisplayName = "Saved tool", ExternalId = "saved-tool", Provider = new(Provider.Name, Provider.ScopeRef, Provider.Namespace) }
        };
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(ResourceScopeRef owner, ResourceNamespace ns, string name, CancellationToken token)
        {
            ToolReads++; Assert.AreEqual(Provider.ScopeRef, owner); Assert.AreEqual(Provider.Namespace, ns); Assert.AreEqual("saved-tool", name);
            return Task.FromResult(new ResourceSnapshot<ToolResource>(Tool, "\"tool\""));
        }
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
