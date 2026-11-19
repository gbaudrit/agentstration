using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
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
        var form = rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        Assert.IsFalse(rendered.Find("[data-testid=provider-overview-panel]").HasAttribute("hidden"));
        Assert.IsNotNull(rendered.Find("[data-testid=provider-tools-panel]").Closest("[hidden]"));
        Assert.IsNotNull(rendered.Find("[data-testid=provider-connection-panel]").Closest("[hidden]"));
        await rendered.InvokeAsync(() => navigation.NavigateSection("tools"));
        Assert.IsEmpty(rendered.FindAll(".required-fields-note"));
        Assert.IsTrue(rendered.Find("[data-testid=provider-overview-panel]").HasAttribute("hidden"));
        Assert.IsNull(rendered.Find("[data-testid=provider-tools-panel]").Closest("[hidden]"));
        Assert.IsNotNull(rendered.Find("[data-testid=provider-connection-panel]").Closest("[hidden]"));
        await rendered.InvokeAsync(() => navigation.NavigateSection("definition/connection"));
        Assert.HasCount(1, rendered.FindAll(".resource-header .required-fields-note"));
        Assert.IsTrue(rendered.Find("[data-testid=provider-overview-panel]").HasAttribute("hidden"));
        Assert.IsNotNull(rendered.Find("[data-testid=provider-tools-panel]").Closest("[hidden]"));
        Assert.IsNull(rendered.Find("[data-testid=provider-connection-panel]").Closest("[hidden]"));
        Assert.AreSame(form, rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
    }

    [TestMethod]
    [DataRow("en-US", "Identity")]
    [DataRow("fr-FR", "Identité")]
    public async Task ProviderHeaderSwitchIsADraftAndSaveKeepsTheExistingEtag(string locale, string identity)
    {
        using var culture = new TestCultureScope(locale);
        var client = new ProviderClient();
        using var context = Context(client, false);
        var cut = context.Render<ToolProviderEditor>(p => p.Add(c => c.Name, "microsoft-learn"));
        Assert.Contains(identity, cut.Find(".resource-identity-summary").TextContent);
        cut.Find(".resource-header input[role=switch]").Change(false);
        Assert.AreEqual(0, client.Writes);
        Assert.IsTrue(client.Provider.Definition.Enabled);
        Assert.HasCount(1, cut.FindAll(".resource-details-main > .unsaved-changes-bar"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/connection"));
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

    private static BunitContext Context(ProviderClient client, bool create)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(client);
        var uri = create ? "http://localhost/tools/providers/new" : "http://localhost/tools/providers/microsoft-learn";
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/configuration", "definition/connection", "tools" })
        {
            var target = uri + "#" + section;
            module.Setup<string>("navigate", i => i.Arguments[0] is string value && value.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(target);
        }
        return context;
    }

    private sealed class ProviderClient : IToolsClient
    {
        public ToolProviderResource Provider { get; private set; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new ResourceMetadata { Name = "microsoft-learn" },
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

        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ToolProviderResource>(Provider, "\"initial\""));
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolResource>>([]);
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
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
