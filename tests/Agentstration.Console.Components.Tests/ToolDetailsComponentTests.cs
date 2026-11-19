using System.Globalization;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.JsonSchema;
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
[DoNotParallelize]
public sealed class ToolDetailsComponentTests
{
    [TestMethod]
    public void ShowsCapabilityInputFieldsOutputFieldsAndExactSourceSchemas()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"deliveryKey":{"type":"string","description":"Stable key supplied by provider.","maxLength":256},"actionUrl":{"type":"string","pattern":"^/"}},"required":["deliveryKey"]}
                """),
            Output = JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"notificationId":{"type":"string","description":"Created notification identifier."}},"required":["notificationId"]}
                """)
        });

        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        Assert.Contains("Create notification", rendered.Find(".resource-identity-summary").TextContent);
        Assert.Contains("create-notification", rendered.Find(".resource-identity-summary").TextContent);
        Assert.Contains("provider", rendered.Find(".resource-configuration-summary").TextContent);
        Assert.IsNotNull(rendered.Find(".resource-header input[role=switch]"));
        Assert.AreEqual("What this tool does", rendered.Find("#tool-capability-heading").TextContent);
        StringAssert.Contains(rendered.Find(".tool-capability").TextContent, "Create one durable notification.");
        Assert.AreEqual("Inputs", rendered.Find("#tool-input-heading").TextContent);
        Assert.AreEqual("Outputs", rendered.Find("#tool-output-heading").TextContent);
        var inputFields = rendered.FindAll(".tool-contract").First().QuerySelectorAll(".tool-field");
        Assert.AreEqual(2, inputFields.Length);
        StringAssert.Contains(inputFields[0].TextContent, "deliveryKey");
        StringAssert.Contains(inputFields[0].TextContent, "Required");
        StringAssert.Contains(inputFields[0].TextContent, "Stable key supplied by provider.");
        StringAssert.Contains(inputFields[0].TextContent, "Maximum length: 256");
        StringAssert.Contains(inputFields[1].TextContent, "Optional");
        StringAssert.Contains(inputFields[1].TextContent, "Pattern: ^/");
        StringAssert.Contains(rendered.FindAll(".tool-contract")[1].TextContent, "notificationId");
        Assert.AreEqual(2, rendered.FindAll(".tool-raw-schema pre").Count);
        StringAssert.Contains(rendered.FindAll(".tool-raw-schema pre")[0].TextContent, "\"required\"");
        StringAssert.Contains(rendered.FindAll(".tool-raw-schema pre")[1].TextContent, "\"notificationId\"");
    }

    [TestMethod]
    public void MissingAndNonObjectSchemasHaveHonestFallbacks()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"oneOf":[{"type":"string"},{"type":"number"}]}"""),
            Output = null
        }, description: null);

        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        StringAssert.Contains(rendered.Find(".tool-capability").TextContent, "No capability description was provided");
        StringAssert.Contains(rendered.FindAll(".tool-contract")[0].TextContent, "cannot be summarized");
        StringAssert.Contains(rendered.FindAll(".tool-contract")[1].TextContent, "No schema was provided");
        Assert.AreEqual(1, rendered.FindAll(".tool-raw-schema").Count);
        StringAssert.Contains(rendered.Find(".tool-raw-schema pre").TextContent, "oneOf");
    }

    [TestMethod]
    public void FrenchChromeKeepsProviderDescriptionsVerbatim()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"key":{"type":"string","description":"Provider-authored English description."}},"required":["key"]}""")
        });

        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        Assert.AreEqual("Ce que fait cet outil", rendered.Find("#tool-capability-heading").TextContent);
        Assert.AreEqual("Entrées", rendered.Find("#tool-input-heading").TextContent);
        Assert.AreEqual("Sorties", rendered.Find("#tool-output-heading").TextContent);
        StringAssert.Contains(rendered.Find(".tool-field").TextContent, "Obligatoire");
        StringAssert.Contains(rendered.Find(".tool-field").TextContent, "Provider-authored English description.");
        StringAssert.Contains(rendered.Find(".tool-capability").TextContent, "Create one durable notification.");
    }

    [TestMethod]
    public void AcquisitionToolOpensInExecutionAndLinksTheReturnedArtifactInContext()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext(new ToolSchema { Input = ToolDefinitionAcquisitionTarget.InputSchema });
        context.Services.AddSingleton<IConsoleNavigationContext>(new InstanceNavigationContext());
        var client = context.Services.GetRequiredService<ToolClient>();
        client.ExecutionOutput = JsonSerializer.Deserialize<JsonElement>("""
            {"acquisitionId":"acquisition-2","artifact":{"artifactId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","kind":"durable"}}
            """);
        var workspace = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo($"/tools/acquisition-tool?scopeRef={Uri.EscapeDataString(workspace.Value)}&context=%2Finstance#execution");
        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "acquisition-tool"));

        rendered.WaitForAssertion(() => Assert.AreEqual("execution", rendered.FindComponent<ResourceNavigation>().Instance.CurrentPath));
        rendered.Find("[data-testid='tool-run-submit']").Click();

        var artifact = rendered.WaitForElement("[data-testid='tool-run-open-artifact']");
        Assert.AreEqual("Ouvrir l’artefact", artifact.TextContent);
        Assert.AreEqual("/artifacts/durable/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?context=%2Finstance", artifact.GetAttribute("href"));
        Assert.AreEqual(workspace, client.ExecutionScope);
        Assert.AreEqual(ToolRunMode.Execute, client.RunRequests.Single().Mode);
    }

    [TestMethod]
    public async Task ExecutionTabReusesSchemaEditorAndSimulatesByDefault()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"dryRun":{"type":"boolean"}},"additionalProperties":false}""")
        });
        var client = context.Services.GetRequiredService<ToolClient>();
        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        Assert.AreEqual("overview", rendered.FindComponent<ResourceNavigation>().Instance.CurrentPath);
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("execution"));

        Assert.AreEqual("execution", rendered.FindComponent<ResourceNavigation>().Instance.CurrentPath);
        Assert.IsNotNull(rendered.Find("[data-testid='schema-input-editor']"));
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-simulate-mode']").ClassName, "selected");
        var dryRun = rendered.Find("[data-schema-path='$.dryRun'] input");
        Assert.IsTrue(dryRun.HasAttribute("checked"));
        Assert.IsTrue(dryRun.HasAttribute("disabled"));
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-dry-run-managed']").TextContent, "automatically");
        rendered.Find("[data-testid='tool-run-submit']").Click();

        rendered.WaitForAssertion(() => Assert.HasCount(1, client.RunRequests));
        Assert.AreEqual(ToolRunMode.Simulate, client.RunRequests[0].Mode);
        Assert.IsTrue(client.RunRequests[0].Arguments.GetProperty("dryRun").GetBoolean());
        Assert.IsNotNull(rendered.Find("[data-testid='tool-run-result']"));
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-result']").TextContent, "Provider invokedYes");
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-output']").TextContent, "dryRun");

        var schemaEditor = rendered.FindComponent<JsonSchemaInputEditor>().Instance;
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("execution"));
        Assert.AreSame(schemaEditor, rendered.FindComponent<JsonSchemaInputEditor>().Instance);
        Assert.IsNotNull(rendered.Find("[data-testid='tool-run-result']"));
        rendered.Find("[data-testid='tool-run-execute-mode']").Click();
        dryRun = rendered.Find("[data-schema-path='$.dryRun'] input");
        Assert.IsFalse(dryRun.HasAttribute("checked"));
        Assert.IsFalse(dryRun.HasAttribute("disabled"));
        Assert.HasCount(0, rendered.FindAll("[data-testid='tool-run-dry-run-managed']"));
    }

    [TestMethod]
    public async Task RealExecutionPutsToolOutputBeforeTechnicalDetails()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{},"additionalProperties":false}""")
        });
        var client = context.Services.GetRequiredService<ToolClient>();
        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("execution"));
        rendered.Find("[data-testid='tool-run-execute-mode']").Click();
        rendered.Find("[data-testid='tool-run-submit']").Click();

        rendered.WaitForAssertion(() => Assert.HasCount(1, client.RunRequests));
        Assert.AreEqual(ToolRunMode.Execute, client.RunRequests[0].Mode);
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-output']").TextContent, "created");
        Assert.IsNotNull(rendered.Find(".tool-run-diagnostics"));
    }

    [TestMethod]
    public async Task SimulationIsDisabledWhenDryRunIsNotDeclared()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema
        {
            Input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{},"additionalProperties":false}""")
        });
        var rendered = context.Render<ToolDetails>(parameters => parameters.Add(page => page.Name, "create-notification"));

        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("execution"));

        Assert.IsTrue(rendered.Find("[data-testid='tool-run-simulate-mode']").HasAttribute("disabled"));
        StringAssert.Contains(rendered.Find("[data-testid='tool-run-execute-mode']").ClassName, "selected");
        StringAssert.Contains(rendered.Find(".tool-run-mode-unavailable").TextContent, "does not declare a boolean dryRun");
    }

    [TestMethod]
    public void HeaderActivationPreservesTheEtagAndRefreshesSavedConfiguration()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object" }) });
        var client = context.Services.GetRequiredService<ToolClient>();
        var cut = context.Render<ToolDetails>(p => p.Add(c => c.Name, "create-notification"));
        cut.Find(".resource-header input[role=switch]").Change(false);
        cut.WaitForAssertion(() => Assert.AreEqual(false, client.EnabledRequest));
        Assert.AreEqual("\"etag\"", client.LastEtag);
        Assert.IsFalse(cut.Find(".resource-header input[role=switch]").HasAttribute("checked"));
        Assert.Contains("No", cut.Find(".resource-configuration-summary").TextContent);
    }

    [TestMethod]
    public async Task CatalogInspectionUsesCommonIdentityStateAndSavedYaml()
    {
        using var context = CreateContext(new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object" }) });
        var client = context.Services.GetRequiredService<ToolClient>();
        client.Resource = client.Resource with { Metadata = client.Resource.Metadata with { Tags = new Dictionary<string, string> { ["environment"] = "fixture" } } };
        var cut = context.Render<ToolDetails>(p => p.Add(c => c.Name, "create-notification"));
        Assert.Contains("environment: fixture", cut.Find(".resource-identity-summary .labels-summary").TextContent);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance; var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.IsTrue(form.ReadOnly); Assert.IsTrue(cut.FindComponent<ResourceIdentityEditor>().Instance.ReadOnly);
        Assert.AreSame(adapter.SectionState, form.SectionState); Assert.AreSame(adapter.SectionPresentations, form.Sections);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        Assert.IsNotNull(cut.Find(".resource-yaml-editor textarea[readonly]"));
        Assert.Contains("create-notification", cut.Find(".resource-yaml-editor textarea").GetAttribute("value")!);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task InvalidExecutionJsonAndItsEditorSurviveMetadataAndContractRefreshUntilExplicitReset()
    {
        var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { text = new { type = "string" } } });
        using var context = CreateContext(new ToolSchema { Input = input }); var client = context.Services.GetRequiredService<ToolClient>();
        var cut = context.Render<ToolDetails>(p => p.Add(c => c.Name, "create-notification"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("execution"));
        cut.Find("[data-testid=schema-raw-mode]").Click(); cut.Find("textarea.runner-json").Input("{ invalid json");
        var editor = cut.FindComponent<JsonSchemaInputEditor>().Instance;
        Assert.IsTrue(cut.Find("[data-testid=tool-run-submit]").HasAttribute("disabled"));
        client.Resource = client.Resource with { Generation = 2, Definition = client.Resource.Definition with { DisplayName = "Refreshed tool" } };
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(editor, cut.FindComponent<JsonSchemaInputEditor>().Instance);
        Assert.AreEqual("{ invalid json", cut.Find("textarea.runner-json").GetAttribute("value"));
        var newInput = JsonSerializer.SerializeToElement(new { type = "object", properties = new { replacement = new { type = "string" } } });
        client.Resource = client.Resource with { Generation = 3, Definition = client.Resource.Definition with { Schema = new() { Input = newInput } } };
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(editor, cut.FindComponent<JsonSchemaInputEditor>().Instance); Assert.AreEqual(input.GetRawText(), editor.Schema!.Value.GetRawText());
        Assert.AreEqual("{ invalid json", cut.Find("textarea.runner-json").GetAttribute("value"));
        Assert.IsTrue(cut.Find("[data-testid=tool-run-submit]").HasAttribute("disabled"));
        Assert.IsNotNull(cut.Find(".resource-details-feedback .form-alert-warning"));
        cut.Find(".resource-section-actions button").Click();
        Assert.AreNotSame(editor, cut.FindComponent<JsonSchemaInputEditor>().Instance);
        Assert.AreEqual(newInput.GetRawText(), cut.FindComponent<JsonSchemaInputEditor>().Instance.Schema!.Value.GetRawText());
        Assert.IsEmpty(cut.FindAll(".resource-details-feedback .form-alert-warning"));
        Assert.IsFalse(cut.Find("[data-testid=tool-run-submit]").HasAttribute("disabled")); Assert.IsEmpty(client.RunRequests);
    }

    [TestMethod]
    public async Task ActivationKeepsVisitedInvalidExecutionInputAndItsOriginalWriteEtag()
    {
        using var context = CreateContext(new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object" }) });
        var client = context.Services.GetRequiredService<ToolClient>(); var cut = context.Render<ToolDetails>(p => p.Add(c => c.Name, "create-notification"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("execution"));
        cut.Find("[data-testid=schema-raw-mode]").Click(); cut.Find("textarea.runner-json").Input("{ invalid json"); var editor = cut.FindComponent<JsonSchemaInputEditor>().Instance;
        cut.Find(".resource-header input[role=switch]").Change(false);
        Assert.AreEqual("\"etag\"", client.LastEtag); Assert.AreEqual(false, client.EnabledRequest);
        Assert.AreSame(editor, cut.FindComponent<JsonSchemaInputEditor>().Instance); Assert.AreEqual("{ invalid json", cut.Find("textarea.runner-json").GetAttribute("value"));
        Assert.IsTrue(cut.Find("[data-testid=tool-run-submit]").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task PolicyDraftSurvivesToolRefreshAndRecoverableFailure()
    {
        using var context = CreateContext(new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object" }) }); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var client = context.Services.GetRequiredService<ToolClient>(); var cut = context.Render<ToolDetails>(p => p.Add(c => c.Name, "create-notification"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(x => x.DraftChanged).InvokeAsync(true));
        client.Resource = client.Resource with { Generation = 2 };
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        client.ReadError = new AgentstrationApiException("Unavailable", "fixture", System.Net.HttpStatusCode.ServiceUnavailable);
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsTrue(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.HasContent);
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostAccessOrReplacementClearsPrivateExecutionYamlAndPolicyState(bool replace)
    {
        using var context = CreateContext(new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object" }) }); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var client = context.Services.GetRequiredService<ToolClient>(); var cut = context.Render<ToolDetails>(p => p.Add(c => c.Name, "create-notification"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("execution"));
        await cut.InvokeAsync(() => cut.FindComponent<JsonSchemaInputEditor>().Instance.ValueChanged.InvokeAsync(JsonSerializer.SerializeToElement(new { input = "private pending fixture" })));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        await cut.InvokeAsync(() => cut.FindComponent<Stub<ResourceAccessPanel>>().Instance.Parameters.Get(x => x.DraftChanged).InvokeAsync(true));
        if (replace) client.Resource = client.Resource with { Uid = Guid.NewGuid() };
        else client.ReadError = new AgentstrationApiException("Denied", "fixture", System.Net.HttpStatusCode.Forbidden);
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.IsEmpty(cut.FindAll(".resource-form, .resource-yaml-editor, .tool-runner"));
        Assert.DoesNotContain("private pending fixture", cut.Markup);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExecutionAndDefinitionWritesKeepIndependentPermissions(bool execute)
    {
        using var context = CreateContext(new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object" }) });
        context.Services.AddSingleton(new ConsoleContextState(new ToolPermissions(execute)));
        var client = context.Services.GetRequiredService<ToolClient>(); var cut = context.Render<ToolDetails>(p => p.Add(c => c.Name, "create-notification"));
        Assert.AreEqual(execute, cut.Find(".resource-header input[role=switch]").HasAttribute("disabled"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("execution"));
        Assert.AreEqual(!execute, cut.Find("[data-testid=tool-run-submit]").HasAttribute("disabled"));
        cut.Find("[data-testid=tool-run-submit]").Click(); Assert.AreEqual(execute ? 1 : 0, client.RunRequests.Count);
        if (execute) Assert.AreEqual(ResourceScopeRef.Instance, client.ExecutionScope);
    }

    private sealed class ToolPermissions(bool execute) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Fixture user", "/instance", ConsoleContextKind.Instance, Guid.Empty, "dev", "Development", Guid.Empty, "default", "Default workspace",
            new HashSet<string>(StringComparer.Ordinal) { "resources/read", execute ? "runs/execute" : "resources/write" }, [], []));
    }

    [TestMethod]
    public void MissingDiscoveryDoesNotInventAnUnavailableStatusOrAvailability()
    {
        using var context = CreateContext(new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object" }) });
        var client = context.Services.GetRequiredService<ToolClient>(); client.Resource = client.Resource with { Definition = client.Resource.Definition with { Discovery = null } };
        var cut = context.Render<ToolDetails>(p => p.Add(c => c.Name, "create-notification"));
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.IsNull(adapter.StatusText); Assert.AreEqual(UiStatus.Neutral, adapter.Status);
        Assert.Contains("Available", cut.Find(".resource-configuration-summary").TextContent);
        Assert.Contains("—", cut.Find(".resource-configuration-summary").TextContent);
        Assert.IsEmpty(cut.FindAll(".metric-card"));
    }

    private static BunitContext CreateContext(ToolSchema schema, string? description = "Create one durable notification.")
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "execution", "access", "yaml" })
            module.Setup<string>("navigate", c => c.Arguments[0] is string url && url.EndsWith("#" + path, StringComparison.Ordinal)).SetResult("http://localhost/#" + path);
        var client = new ToolClient(new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "Tool",
            Uid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new ResourceMetadata { Name = "create-notification" },
            Definition = new ToolResourceProperties
            {
                DisplayName = "Create notification",
                Description = description,
                Schema = schema,
                Provider = new ResourceReference("provider"),
                ExternalId = "create-notification",
                Discovery = new ToolDiscoveryState
                {
                    Available = true,
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow
                }
            }
        });
        context.Services.AddSingleton(client);
        context.Services.AddSingleton<IToolsClient>(client);
        return context;
    }

    private sealed class ToolClient(ToolResource tool) : IToolsClient
    {
        public ToolResource Resource { get; set; } = tool;
        public string ReadEtag { get; set; } = "\"etag\"";
        public Exception? ReadError { get; set; }
        public string? LastEtag { get; private set; }
        public bool? EnabledRequest { get; private set; }
        public List<RunToolRequest> RunRequests { get; } = [];
        public ResourceScopeRef? ExecutionScope { get; private set; }
        public JsonElement? ExecutionOutput { get; set; }

        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) =>
            ReadError is { } error ? Task.FromException<ResourceSnapshot<ToolResource>>(error) : Task.FromResult(new ResourceSnapshot<ToolResource>(Resource with { Metadata = Resource.Metadata with { Name = name } }, ReadEtag));

        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            ReadError is { } error ? Task.FromException<ResourceSnapshot<ToolResource>>(error) : Task.FromResult(new ResourceSnapshot<ToolResource>(Resource with { Metadata = Resource.Metadata with { Name = name, Namespace = @namespace }, ScopeRef = scopeRef }, ReadEtag));

        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken)
        {
            LastEtag = etag; EnabledRequest = enabled;
            Resource = Resource with { Definition = Resource.Definition with { Enabled = enabled } };
            return Task.FromResult(new ResourceSnapshot<ToolResource>(Resource, "\"etag-2\""));
        }

        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RunToolResponse> RunToolAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, RunToolRequest request, CancellationToken cancellationToken)
        {
            ExecutionScope = scopeRef;
            return RunToolAsync(@namespace, name, request, cancellationToken);
        }

        public Task<RunToolResponse> RunToolAsync(ResourceNamespace @namespace, string name, RunToolRequest request, CancellationToken cancellationToken)
        {
            RunRequests.Add(request);
            return Task.FromResult(new RunToolResponse(
                request.Mode,
                request.Mode == ToolRunMode.Simulate ? "simulated" : "completed",
                name,
                @namespace.Value,
                "provider",
                true,
                false,
                [new ToolRunCheck(request.Mode == ToolRunMode.Simulate ? "dry_run" : "execution", "passed", "Completed.")],
                request.Mode == ToolRunMode.Execute
                    ? ExecutionOutput ?? JsonSerializer.SerializeToElement(new { status = "created" })
                    : JsonSerializer.SerializeToElement(new { dryRun = true })));
        }
    }

    private sealed class InstanceNavigationContext : IConsoleNavigationContext
    {
        public string ScopeRef => "/instance";
        public string AddToUri(string uri) => $"{uri}?context=%2Finstance";
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;
        private readonly CultureInfo previousUi = CultureInfo.CurrentUICulture;

        public CultureScope(string culture)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
