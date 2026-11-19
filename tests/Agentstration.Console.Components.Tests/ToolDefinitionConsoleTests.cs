using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Tools;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolDefinitionConsoleTests
{
    [TestMethod]
    public void DefinitionsPageListsFlowBackedMcpTools()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolDefinitionsClient>(new DefinitionClient());
        context.Services.AddAgentstrationWebComponents();
        context.Services.GetRequiredService<IConsoleNavigationContext>().SetSelectedScopeRef("/workspaces/11111111-1111-1111-1111-111111111111");

        var rendered = context.Render<ToolDefinitions>();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Send notification");
            StringAssert.Contains(rendered.Markup, "notification-flow");
            var edit = rendered.Find(".collection-primary-actions a");
            Assert.Contains("/tools/definitions/notification.send?namespace=default", edit.GetAttribute("href")!);
            Assert.HasCount(0, rendered.FindAll(".collection-actions-menu"), "Editing remains directly available.");
            StringAssert.Contains(rendered.Markup, "/tools/definitions/notification.send?namespace=default");
            Assert.AreEqual("/tools/definitions/new?context=%2Fworkspaces%2F11111111-1111-1111-1111-111111111111",
                rendered.Find("a.button-primary").GetAttribute("href"));
        });
    }

    [TestMethod]
    public void EditorLoadsReadOnlyContractsWhenAFlowIsSelected()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolDefinitionsClient>(new DefinitionClient());
        var flows = new FlowClient();
        context.Services.AddSingleton<IFlowApiClient>(flows);
        context.Services.AddSingleton<ISchemasClient>(new SchemasClient());

        var creationContext = new CreationContext();
        context.Services.AddSingleton<IConsoleNavigationContext>(creationContext);
        context.Services.AddSingleton(new ConsoleContextState(creationContext));
        var rendered = context.Render<NewToolDefinition>();
        rendered.Find("[data-testid='tool-definition-display-name']").Change("Contract Tool");
        rendered.Find("[data-testid='creation-wizard-next']").Click();
        rendered.WaitForElement("[data-testid='tool-definition-flow']").Change("/workspaces/11111111-1111-1111-1111-111111111111|default|contract-flow");

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("1.2.0", flows.RequestedVersion);
            StringAssert.Contains(rendered.Markup, "Read-only contracts provided by published Flow version 1.2.0");
            Assert.HasCount(0, rendered.FindAll(".flow-schema-mode"));
            Assert.IsTrue(rendered.Find("[data-testid='tool-definition-input-schema']") is not null);
        });

        rendered.Find("[data-testid='tool-definition-contract-output-tab']").Click();
        rendered.WaitForAssertion(() =>
            Assert.IsTrue(rendered.Find("[data-testid='tool-definition-output-schema']") is not null));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublicationChangesRequireNavigationConfirmationUntilReverted(bool changePublishFlag)
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolDefinitionsClient>(new DefinitionClient());
        context.Services.AddSingleton<IFlowApiClient>(new FlowClient());
        context.Services.AddSingleton<ISchemasClient>(new SchemasClient());
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/tools/definitions/notification.send?namespace=default");
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        var url = navigation.Uri.Split('#')[0] + "#definition/configuration";
        module.Setup<string>("navigate", _ => true).SetResult(url);
        module.Setup<string>("currentLocation", _ => true).SetResult(url);
        var rendered = context.Render<ToolDefinitionEditor>(parameters => parameters.Add(value => value.Name, "notification.send"));
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty));
        var version = rendered.FindComponents<FormField>().Single(field => field.Instance.Label == "ToolDefinition version").Find("input");
        var publish = () => rendered.FindAll("label").Single(label => label.TextContent.Trim() == "Publish version").QuerySelector("input")!;
        var originalVersion = version.GetAttribute("value");
        if (changePublishFlag) publish().Change(true);
        else version.Change("1.0.1");
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty));
        var originalLocation = navigation.Uri;
        await rendered.InvokeAsync(() => navigation.NavigateTo("/tools/definitions"));
        rendered.WaitForElement("[role=alertdialog]");
        Assert.AreEqual(originalLocation, navigation.Uri);
        await rendered.Find("[role=alertdialog] .button-secondary").ClickAsync(new());
        Assert.IsEmpty(rendered.FindAll("[role=alertdialog]"));
        Assert.IsTrue(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
        if (changePublishFlag) publish().Change(false);
        else version.Change(originalVersion);
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty));
        await rendered.InvokeAsync(() => navigation.NavigateTo("/tools/definitions"));
        Assert.IsTrue(navigation.Uri.EndsWith("/tools/definitions", StringComparison.Ordinal));
        Assert.IsEmpty(rendered.FindAll("[role=alertdialog]"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SavedOverviewAndMetadataSaveKeepTheDeclaredFlowAndHiddenBindings(bool sharedSchema)
    {
        using var culture = new TestCultureScope("en-US");
        var resource = SavedDefinition();
        var original = (FlowToolDefinitionProperties)resource.Definition;
        resource = resource with { Definition = original with
        {
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
            FixedArguments = JsonSerializer.SerializeToElement(new { query = "fixed query" }),
            InputSchemaBinding = sharedSchema
                ? new() { Reference = new() { Name = "public-contract", Version = "1.0.0", ScopeRef = resource.ScopeRef, Namespace = resource.Namespace } }
                : new() { Inline = new() { Format = "json-schema", Content = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }) } }
        } };
        var client = new DefinitionClient { Resource = resource };
        using var context = SavedContext(client);
        var cut = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, resource.Name));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        Assert.Contains("Saved Tool", cut.Find(".resource-identity-summary").TextContent);
        await Select(cut, "definition/configuration");
        cut.FindComponent<ResourceIdentityFields>().Find("input:not([disabled])").Input("Local Tool draft");
        await Select(cut, "overview");
        Assert.Contains("Saved Tool", cut.Find(".resource-identity-summary").TextContent);
        Assert.DoesNotContain("Local Tool draft", cut.Find(".resource-identity-summary").TextContent);
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        await cut.Find("#tool-definition-resource-form").SubmitAsync();
        Assert.IsNotNull(client.Updated);
        var saved = Assert.IsInstanceOfType<FlowToolDefinitionProperties>(client.Updated.Properties);
        Assert.AreEqual("Local Tool draft", saved.DisplayName);
        Assert.AreEqual(((FlowToolDefinitionProperties)resource.Definition).Flow, saved.Flow);
        Assert.IsNull(saved.Flow.ScopeRef, "A metadata edit must not rewrite the declared relative reference.");
        Assert.AreEqual(resource.Definition.FixedArguments?.GetRawText(), saved.FixedArguments?.GetRawText());
        Assert.AreEqual(resource.Definition.InputSchema.GetRawText(), saved.InputSchema.GetRawText());
        Assert.AreEqual(JsonSerializer.Serialize(resource.Definition.Bindings), JsonSerializer.Serialize(saved.Bindings));
        Assert.AreEqual(resource.Definition.InputSchemaBinding, saved.InputSchemaBinding);
        Assert.AreEqual("definition-etag", client.LastEtag);
        Assert.AreEqual(1, client.Reads);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task DiscardRestoresTheSavedActiveFlowContractAndHeaderActivation()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new DefinitionClient { Resource = SavedDefinition() };
        using var context = SavedContext(client);
        var cut = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, client.Resource!.Name));
        await Select(cut, "definition/configuration");
        cut.Find(".resource-header input[role=switch]").Change(false);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await Select(cut, "overview");
        await cut.Find(".unsaved-changes-bar .button-secondary").ClickAsync();
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsTrue(cut.Find(".resource-header input[role=switch]").HasAttribute("checked"));
        await Select(cut, "definition/implementation");
        Assert.Contains("1.2.0", cut.Find("[data-testid=tool-definition-contract]").ParentElement!.TextContent);
        Assert.IsNull(client.Updated);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManagedAndInheritedDefinitionsKeepASavedOverviewWithoutMutationCommands(bool managed)
    {
        var resource = SavedDefinition();
        resource = managed ? resource with { Governance = ResourceGovernance.Pack("fixture", "fixture/tool", "1.0.0") } : resource with { ScopeRef = ResourceScopeRef.Instance };
        var client = new DefinitionClient { Resource = resource };
        using var context = SavedContext(client);
        var cut = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, resource.Name));
        Assert.Contains("Saved Tool", cut.Find(".resource-identity-summary").TextContent);
        Assert.IsTrue(cut.Find(".resource-header input[role=switch]").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll("a[data-resource-section^='definition/'], .resource-header button.button-danger, .unsaved-changes-bar"));
        await cut.Find("#tool-definition-resource-form").SubmitAsync();
        Assert.IsNull(client.Updated);
    }

    [TestMethod]
    public async Task DefinitionHistoryIsLoadedOnlyForItsExactSavedOwner()
    {
        var client = new DefinitionClient { Resource = SavedDefinition() };
        using var context = SavedContext(client);
        var cut = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, client.Resource!.Name));
        Assert.AreEqual(0, client.HistoryReads);
        await Select(cut, "versions");
        Assert.AreEqual(client.Resource!.ScopeRef, client.HistoryOwner);
        Assert.AreEqual(client.Resource!.ScopeRef, client.UsageOwner);
        await Select(cut, "usages"); await Select(cut, "overview");
        Assert.AreEqual(1, client.HistoryReads);
        Assert.AreEqual(1, client.Reads);
    }

    private static ToolDefinitionResource SavedDefinition() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolDefinition,
        Metadata = new() { Name = "notification.send" }, Uid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        Definition = new FlowToolDefinitionProperties
        {
            DisplayName = "Saved Tool", InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } } }),
            Flow = new() { Name = "contract-flow", UseActiveVersion = true }
        }
    };
    private static BunitContext SavedContext(DefinitionClient client)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolDefinitionsClient>(client); context.Services.AddSingleton<IFlowApiClient>(new FlowClient()); context.Services.AddSingleton<ISchemasClient>(new SchemasClient());
        context.Services.AddSingleton<IConsoleNavigationContext>(new CreationContext());
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/tools/definitions/notification.send?scopeRef=" + Uri.EscapeDataString(client.Resource!.ScopeRef!.Value.Value));
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js"); var url = nav.Uri.Split('#')[0];
        foreach (var path in new[] { "overview", "definition/configuration", "definition/implementation", "versions", "usages" }) module.Setup<string>("navigate", call => call.Arguments[0] is string target && target.EndsWith("#" + path, StringComparison.Ordinal)).SetResult(url + "#" + path);
        return context;
    }
    private static Task Select(IRenderedComponent<ToolDefinitionEditor> cut, string section) => cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection(section));

    private sealed class CreationContext : IConsoleNavigationContext, IConsoleContextProvider
    {
        public string ScopeRef => "/workspaces/11111111-1111-1111-1111-111111111111";
        public string AddToUri(string uri) => uri;
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(NewToolDefinitionTests.SnapshotFor(ResourceScopeRef.Parse(ScopeRef)));
    }

    private sealed class DefinitionClient : IToolDefinitionsClient
    {
        public ToolDefinitionResource? Resource { get; set; }
        public Agentstration.Tools.Contracts.PutToolDefinitionRequest? Updated { get; private set; }
        public string? LastEtag { get; private set; }
        public int Reads { get; private set; }
        public int HistoryReads { get; private set; }
        public ResourceScopeRef? HistoryOwner { get; private set; }
        public ResourceScopeRef? UsageOwner { get; private set; }
        public Task<ResourceSnapshot<ToolDefinitionResource>> GetAsync(ResourceScopeRef owner, string name, ResourceNamespace ns, CancellationToken ct) => GetAsync(name, ns, ct);
        public Task<IReadOnlyList<ToolDefinitionVersionResource>> GetVersionsAsync(ResourceScopeRef owner, string name, ResourceNamespace ns, CancellationToken ct) { HistoryOwner = owner; HistoryReads++; return Task.FromResult<IReadOnlyList<ToolDefinitionVersionResource>>([]); }
        public Task<IReadOnlyList<ToolDefinitionUsage>> GetUsagesAsync(ResourceScopeRef owner, string name, ResourceNamespace ns, CancellationToken ct) { UsageOwner = owner; return Task.FromResult<IReadOnlyList<ToolDefinitionUsage>>([]); }

        public Task<IReadOnlyList<ToolDefinitionResource>> GetAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ToolDefinitionResource>>([
            new ToolDefinitionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolDefinition,
                Metadata = new ResourceMetadata { Name = "notification.send" },
                ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                Definition = new FlowToolDefinitionProperties
                {
                    DisplayName = "Send notification",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                    Flow = new ToolDefinitionFlowTarget { Name = "notification-flow" }
                }
            }
        ]);

        public async Task<ResourceSnapshot<ToolDefinitionResource>> GetAsync(string name, ResourceNamespace @namespace, CancellationToken cancellationToken)
        {
            Reads++;
            if (Resource is not null) return new(Resource, "definition-etag");
            var resource = (await GetAsync(cancellationToken)).Single(value => value.Name == name);
            return new(resource with
            {
                Definition = ((FlowToolDefinitionProperties)resource.Definition) with
                { Flow = new ToolDefinitionFlowTarget { Name = "contract-flow" } }
            }, "definition-etag");
        }
        public Task<ResourceSnapshot<ToolDefinitionResource>> CreateAsync(Agentstration.Tools.Contracts.CreateToolDefinitionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> UpdateAsync(string name, ResourceNamespace @namespace, Agentstration.Tools.Contracts.PutToolDefinitionRequest request, string etag, CancellationToken cancellationToken)
        {
            Updated = request; LastEtag = etag; Resource = Resource! with { Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<ToolDefinitionResource>(Resource, "saved-etag"));
        }
        public Task<ResourceSnapshot<ToolDefinitionResource>> SetEnabledAsync(string name, ResourceNamespace @namespace, bool enabled, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string name, ResourceNamespace @namespace, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FlowClient : IFlowApiClient
    {
        private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { query = new { type = "string" } }
        });
        private static readonly JsonElement OutputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { result = new { type = "string" } }
        });
        private static readonly FlowGraphDefinition Graph = new()
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", Schema = InputSchema },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success, Schema = OutputSchema }
            ]
        };

        public string? RequestedVersion { get; private set; }

        public Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowSummary>>([
                new("contract-flow", "Contract Flow", "flow", "1.2.0", "Published", 0, 0, DateTimeOffset.UtcNow)
                {
                    ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                    ActiveVersion = "1.2.0"
                }
            ]);

        public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(ResourceNamespace @namespace,
            string flowId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowVersionResponse>>([
                new(flowId, "1.2.0", null, new WorkflowFlowDefinition("input", [], [], []),
                    new Dictionary<string, string>(), DateTimeOffset.UtcNow, Graph)
            ]);

        public async Task<FlowVersionResponse> GetFlowVersionAsync(ResourceNamespace @namespace, string flowId,
            string version, CancellationToken cancellationToken)
        {
            RequestedVersion = version;
            return (await GetFlowVersionsAsync(@namespace, flowId, cancellationToken)).Single();
        }

        public Task<FlowVersionResponse> GetFlowVersionAsync(ResourceNamespace @namespace, string flowId,
            string version, ResourceScopeRef? scopeRef, CancellationToken cancellationToken) =>
            GetFlowVersionAsync(@namespace, flowId, version, cancellationToken);

        public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> GetFlowSnapshotAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> CreateFlowAsync(CreateFlowRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> UpdateFlowAsync(string flowId, UpdateFlowRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> CreateFlowVersionAsync(string flowId, CreateFlowVersionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<InputRequest>> GetFlowRunInputsAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InputRequest> RespondToFlowRunInputAsync(string runId, string inputId, JsonElement value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CancelFlowRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public Task<FlowDraftResponse> CreateDraftAsync(CreateFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> GetDraftAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> SaveDraftAsync(string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowValidationResponse> ValidateDraftAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowSourceResponse> GetDraftSourceAsync(string flowId, string format, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> ReplaceDraftSourceAsync(string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> PublishDraftAsync(string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CreateDraftRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> CreateDraftFromVersionAsync(string flowId, string version, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SchemasClient : ISchemasClient
    {
        public Task<IReadOnlyList<SchemaResource>> GetSchemasAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SchemaResource>>([]);
        public Task<SchemaFormatValidationResult> ValidateDefinitionAsync(ValidateSchemaDefinitionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(SchemaFormatValidationResult.Valid);
        public Task<SchemaFormatValidationResult> ValidateInstanceAsync(ValidateSchemaInstanceRequest request, CancellationToken cancellationToken = default) => Task.FromResult(SchemaFormatValidationResult.Valid);
        public Task<IReadOnlyList<SchemaFormatDescriptor>> GetFormatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SchemaResource>> GetSchemaAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SchemaResource>> CreateAsync(CreateSchemaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SchemaResource>> UpdateAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, PutSchemaRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SchemaVersionResource>> GetVersionsAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SchemaVersionResource> GetVersionAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, string version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SchemaVersionResource> PublishAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, PublishSchemaVersionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
