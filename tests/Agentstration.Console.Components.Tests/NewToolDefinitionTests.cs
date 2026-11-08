using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class NewToolDefinitionTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [TestMethod]
    [DataRow("flow", "Instance")]
    [DataRow("relay", "Instance")]
    [DataRow("flow", "Tenant")]
    [DataRow("relay", "Tenant")]
    [DataRow("flow", "Workspace")]
    [DataRow("relay", "Workspace")]
    public void BothKindsUseTheSelectedResourceOwnershipScope(string kind, string scopeKind)
    {
        var owner = scopeKind switch { "Instance" => ResourceScopeRef.Instance, "Tenant" => ResourceScopeRef.Tenant(Guid.Parse("22222222-2222-2222-2222-222222222222")), _ => Scope };
        using var api = new ApiHandler { OwnerScope = owner };
        using var context = Context(api, owner);
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ResourceScopeInventoryApiClient(new HttpClient(api) { BaseAddress = new("https://example.test/") }));
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/tools/definitions/new?context={Uri.EscapeDataString(owner.Value)}&toolKind={kind}&source={(kind == "relay" ? "source.v2.4.0" : "")}");
        var rendered = context.Render<NewToolDefinition>();
        Identity(rendered);
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='tool-definition-continue']").HasAttribute("disabled")));
        var picker = rendered.Find("[data-testid='resource-scope-picker']");
        Assert.IsTrue(picker.QuerySelectorAll("option").Any(option => option.GetAttribute("value") == owner.Value));
        Assert.IsTrue(picker.QuerySelectorAll("option").Last().HasAttribute("disabled"));
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        if (kind == "flow") rendered.WaitForElement("[data-testid='tool-definition-flow']").Change($"{owner}|default|source-flow");
        else rendered.WaitForElement("[data-testid='relay-source']");
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='tool-definition-create-draft']").HasAttribute("disabled")));
        rendered.Find("[data-testid='tool-definition-create-draft']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(api.Created));
        Assert.AreEqual(kind, api.Created!.Properties is RelayToolDefinitionProperties ? "relay" : "flow");
        Assert.IsFalse(api.Created.Properties.Publish);
        Assert.AreEqual(owner, api.Created.ScopeRef);
        Assert.IsTrue(api.Requests.Where(request => request.Path is "/api/flows" or "/api/tools" or "/api/parameters" or "/api/secrets" || request.Path.Contains("/versions", StringComparison.Ordinal)).All(request => request.Context == owner.Value));
        StringAssert.Contains(navigation.Uri, "context=" + Uri.EscapeDataString(owner.Value));
    }

    [TestMethod]
    public async Task CreationFollowsAWorkspaceContextRefreshedAfterTheFormIsRendered()
    {
        using var api = new ApiHandler();
        using var context = Context(api, ResourceScopeRef.Instance);
        var provider = new MutableContextProvider { Snapshot = SnapshotFor(ResourceScopeRef.Instance) };
        var state = new ConsoleContextState(provider);
        context.Services.RemoveAll<ConsoleContextState>();
        context.Services.AddSingleton(state);
        var rendered = context.Render<NewToolDefinition>();
        Identity(rendered);
        Assert.IsFalse(rendered.Find("[data-testid='tool-definition-continue']").HasAttribute("disabled"));

        provider.Snapshot = SnapshotFor(Scope);
        await rendered.InvokeAsync(() => state.LoadAsync(default));

        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='tool-definition-continue']").HasAttribute("disabled")));
        Assert.AreEqual("Search", rendered.Find("[data-testid='tool-definition-display-name']").GetAttribute("value"));
        Assert.HasCount(0, rendered.FindAll("[role='alert']"));
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        rendered.WaitForElement("[data-testid='tool-definition-flow']").Change($"{Scope}|default|source-flow");
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='tool-definition-create-draft']").HasAttribute("disabled")));
        provider.Snapshot = SnapshotFor(ResourceScopeRef.Instance);
        await rendered.InvokeAsync(() => state.LoadAsync(default));
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='tool-definition-continue']").HasAttribute("disabled")));
        Assert.HasCount(0, rendered.FindAll("[data-testid='tool-definition-create-draft']"));

        provider.Snapshot = SnapshotFor(Scope);
        await rendered.InvokeAsync(() => state.LoadAsync(default));
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        Assert.AreEqual("", rendered.WaitForElement("[data-testid='tool-definition-flow']").GetAttribute("value") ?? "");
        Assert.IsFalse(api.Requests.Any(request => request.Method == HttpMethod.Post && request.Path == "/api/tooldefinitions"));
    }

    [TestMethod]
    public async Task CreationFollowsTheWorkspaceQueryWhenTheSamePageIsReused()
    {
        using var api = new ApiHandler();
        using var context = Context(api, ResourceScopeRef.Instance);
        context.Services.RemoveAll<IConsoleNavigationContext>();
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddSingleton(new ConsoleContextState(new MutableContextProvider { Snapshot = SnapshotFor(ResourceScopeRef.Instance) }));
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/tools/definitions/new?context=%2Finstance");
        var rendered = context.Render<NewToolDefinition>();
        Identity(rendered);
        Assert.IsFalse(rendered.Find("[data-testid='tool-definition-continue']").HasAttribute("disabled"));

        await rendered.InvokeAsync(() => navigation.NavigateTo($"/tools/definitions/new?context={Uri.EscapeDataString(Scope.Value)}"));

        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='tool-definition-continue']").HasAttribute("disabled")));
        Assert.AreEqual("Search", rendered.Find("[data-testid='tool-definition-display-name']").GetAttribute("value"));
        Assert.HasCount(0, rendered.FindAll("[role='alert']"));
    }

    [TestMethod]
    public void CreationWaitsForTheSelectedWorkspaceWhenTheLayoutLoadsContextAsynchronously()
    {
        using var api = new ApiHandler();
        using var context = Context(api);
        var navigation = new CreationContext(null);
        var provider = new DeferredContextProvider();
        context.Services.RemoveAll<IConsoleNavigationContext>();
        context.Services.RemoveAll<ConsoleContextState>();
        context.Services.AddSingleton<IConsoleNavigationContext>(navigation);
        context.Services.AddSingleton(new ConsoleContextState(provider));
        var rendered = context.Render<NewToolDefinition>();
        Assert.HasCount(0, rendered.FindAll("[data-testid='tool-definition-continue']"));
        provider.Completion.SetResult(SnapshotFor(Scope));
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='tool-definition-continue']").HasAttribute("disabled")));
        Assert.AreEqual(Scope.Value, navigation.ScopeRef);
        Assert.HasCount(0, api.Requests);
    }

    [TestMethod]
    [DataRow("en-US", "From a Flow", "Tool relay", "Enter a display name", "Choose the ownership scope of this definition")]
    [DataRow("fr-FR", "À partir d’un Flow", "Relais d’un Tool", "Renseignez un nom d’affichage", "Choisissez le périmètre propriétaire de cette définition")]
    public void CreationStartsWithIdentityAndKindWithoutWritingOrLoadingSources(string culture, string flowLabel, string relayLabel, string invalidLabel, string scopeHelp)
    {
        using var locale = new TestCultureScope(culture);
        using var api = new ApiHandler();
        using var context = Context(api);
        var rendered = context.Render<NewToolDefinition>();
        StringAssert.Contains(rendered.Markup, flowLabel);
        StringAssert.Contains(rendered.Markup, relayLabel);
        StringAssert.Contains(rendered.Markup, scopeHelp);
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        StringAssert.Contains(rendered.Find("[role='alert']").TextContent, invalidLabel);
        Assert.IsTrue(rendered.FindAll("[data-testid='tool-definition-identity']").Count == 1);
        Assert.HasCount(0, api.Requests);
        Identity(rendered);
        rendered.Find("[data-testid='tool-definition-initial-version']").Change("2.01.0");
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        Assert.HasCount(0, api.Requests);
    }

    [TestMethod]
    public void FlowDraftPinsItsOwnVersionAndExactFlowVersionWithoutPublishing()
    {
        using var locale = new TestCultureScope("en-US");
        using var api = new ApiHandler();
        using var context = Context(api);
        var rendered = context.Render<NewToolDefinition>();
        Identity(rendered);
        rendered.Find("[data-testid='tool-definition-initial-version']").Change("4.2.0");
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        rendered.WaitForElement("[data-testid='tool-definition-flow']").Change($"{Scope}|default|source-flow");
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='tool-definition-create-draft']").HasAttribute("disabled")));
        rendered.Find("[data-testid='tool-definition-version']").Change("2.3.0");
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "published Flow version 2.3.0"));
        rendered.Find("[data-testid='tool-definition-create-draft']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(api.Created));
        var flow = (FlowToolDefinitionProperties)api.Created!.Properties;
        Assert.AreEqual("4.2.0", flow.Version);
        Assert.AreEqual("2.3.0", flow.Flow.Version);
        Assert.AreEqual(Scope, flow.Flow.ScopeRef);
        Assert.IsFalse(flow.Flow.UseActiveVersion);
        Assert.IsFalse(flow.Publish);
        Assert.HasCount(1, api.Requests.Where(request => request.Method == HttpMethod.Post && request.Path == "/api/tooldefinitions").ToArray());
        Assert.IsFalse(api.Requests.Any(request => request.Method == HttpMethod.Post && request.Path.Contains("/versions", StringComparison.Ordinal)));
        StringAssert.Contains(context.Services.GetRequiredService<NavigationManager>().Uri, "/tools/definitions/search?namespace=default");
    }

    [TestMethod]
    public void RelayConfigurationAndIdentitySurviveBackAndKindChangesAndCreateOnlyADraft()
    {
        using var locale = new TestCultureScope("en-US");
        using var api = new ApiHandler();
        using var context = Context(api);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/tools/definitions/new?toolKind=relay&source=source.v2.4.0");
        var rendered = context.Render<NewToolDefinition>();
        Identity(rendered);
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        rendered.WaitForElement("[data-testid='relay-source']");
        Assert.HasCount(0, rendered.FindAll("[data-testid='relay-publish-review']"));
        Assert.HasCount(0, rendered.FindAll(".section-tabs"));
        rendered.Find("[data-testid='relay-binding-kind']").Change("Literal");
        rendered.WaitForElement(".relay-binding [data-testid='schema-raw-mode']").Click();
        rendered.Find(".relay-binding textarea").Input("\"hr\"");
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='tool-definition-create-draft']").HasAttribute("disabled")));
        rendered.Find("[data-testid='tool-definition-back']").Click();
        Assert.AreEqual("Search", rendered.Find("[data-testid='tool-definition-display-name']").GetAttribute("value"));
        rendered.Find("[data-testid='tool-definition-kind-flow'] input").Change("flow");
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        Assert.HasCount(1, rendered.FindAll("[data-testid='tool-definition-back']"));
        rendered.Find("[data-testid='tool-definition-back']").Click();
        rendered.Find("[data-testid='tool-definition-kind-relay'] input").Change("relay");
        rendered.Find("[data-testid='tool-definition-initial-version']").Change("1.1.0");
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        Assert.AreEqual("\"hr\"", rendered.Find(".relay-binding textarea").GetAttribute("value"));
        rendered.Find("[data-testid='tool-definition-create-draft']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(api.Created));
        var relay = (RelayToolDefinitionProperties)api.Created!.Properties;
        Assert.AreEqual("1.1.0", relay.Version);
        Assert.AreEqual(api.Source.Uid, relay.Target.Uid);
        Assert.AreEqual(api.Source.Generation, relay.Target.Generation);
        Assert.AreEqual("hr", ((LiteralToolArgumentBinding)relay.Bindings.Single()).Value.GetString());
        Assert.IsFalse(relay.Publish);
        Assert.HasCount(1, api.Requests.Where(request => request.Method == HttpMethod.Post).ToArray());
    }

    [TestMethod]
    public void MultipleSourcePublicationsRequireAnExplicitVersionChoice()
    {
        using var locale = new TestCultureScope("en-US");
        using var api = new ApiHandler();
        using var context = Context(api);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/tools/definitions/new?toolKind=relay");
        var rendered = context.Render<NewToolDefinition>();
        Identity(rendered);
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        var select = rendered.WaitForElement("[data-testid='relay-source-tool']");
        select.Change(select.QuerySelectorAll("option")[1].GetAttribute("value"));
        Assert.AreEqual("", rendered.Find("[data-testid='relay-source']").GetAttribute("value"));
        Assert.IsTrue(rendered.Find("[data-testid='tool-definition-create-draft']").HasAttribute("disabled"));
        rendered.Find("[data-testid='relay-source']").Change($"{api.Source.Uid:D}:{api.Source.Generation}");
        Assert.IsFalse(rendered.Find("[data-testid='tool-definition-create-draft']").HasAttribute("disabled"));
        var sourceGroup = rendered.Find("[data-testid='relay-source-tool']").GetAttribute("value");
        rendered.Find("[data-testid='relay-source']").Change("");
        Assert.AreEqual(sourceGroup, rendered.Find("[data-testid='relay-source-tool']").GetAttribute("value"));
        Assert.HasCount(3, rendered.FindAll("[data-testid='relay-source'] option"));
    }

    [TestMethod]
    public void WriteFailureRetainsRelayInputsAndAllowsAnExplicitRetry()
    {
        using var locale = new TestCultureScope("en-US");
        using var api = new ApiHandler { RejectNextCreate = true };
        using var context = Context(api);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/tools/definitions/new?source=source.v2.4.0");
        var rendered = context.Render<NewToolDefinition>();
        Identity(rendered);
        rendered.Find("[data-testid='tool-definition-continue']").Click();
        rendered.WaitForElement("[data-testid='relay-source']");
        rendered.Find("[data-testid='tool-definition-create-draft']").Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "Already exists"));
        Assert.IsNull(api.Created);
        Assert.IsFalse(rendered.Find("[data-testid='tool-definition-create-draft']").HasAttribute("disabled"));
        rendered.Find("[data-testid='tool-definition-create-draft']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(api.Created));
        Assert.AreEqual(api.Source.Uid, ((RelayToolDefinitionProperties)api.Created!.Properties).Target.Uid);
    }

    [TestMethod]
    public void MissingContextCannotCreateToolDefinitions()
    {
        using var api = new ApiHandler();
        using var context = Context(api, ResourceScopeRef.Instance);
        context.Services.RemoveAll<ConsoleContextState>();
        context.Services.AddSingleton(new ConsoleContextState(new UnavailableContextProvider()));
        var rendered = context.Render<NewToolDefinition>();
        Assert.IsTrue(rendered.Find("[data-testid='tool-definition-continue']").HasAttribute("disabled"));
        Assert.HasCount(0, api.Requests);
    }

    private static void Identity(IRenderedComponent<NewToolDefinition> rendered) => rendered.WaitForElement("[data-testid='tool-definition-display-name']").Change("Search");

    private static BunitContext Context(ApiHandler handler, ResourceScopeRef? owner = null)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var navigation = new CreationContext(owner ?? Scope);
        context.Services.AddSingleton<IConsoleNavigationContext>(navigation);
        context.Services.AddSingleton(new ConsoleContextState(navigation));
        var http = new HttpClient(handler) { BaseAddress = new("https://example.test/") };
        context.Services.AddSingleton<IToolDefinitionsClient>(new ToolDefinitionsApiClient(http));
        context.Services.AddSingleton<IFlowApiClient>(new FlowApiClient(http));
        context.Services.AddSingleton<IToolsClient>(new ToolsApiClient(http));
        context.Services.AddSingleton<IParametersClient>(new ParametersApiClient(http));
        context.Services.AddSingleton<ISecretsClient>(new SecretsApiClient(http));
        context.Services.AddSingleton<ISchemasClient>(new SchemasApiClient(http));
        return context;
    }

    internal static ConsoleContextSnapshot SnapshotFor(ResourceScopeRef scope) => new(
        Guid.Empty, "Fixture administrator", scope.Value, scope.Kind switch { ResourceScopeKind.Workspace => ConsoleContextKind.Workspace, ResourceScopeKind.Tenant => ConsoleContextKind.Tenant, _ => ConsoleContextKind.Instance },
        Guid.Empty, "dev", "Development", scope.TargetId ?? Guid.Empty, "default", "Default workspace", new HashSet<string>(), [], []);

    private sealed class UnavailableContextProvider : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromException<ConsoleContextSnapshot>(new HttpRequestException("Context unavailable"));
    }

    private sealed class DeferredContextProvider : IConsoleContextProvider
    {
        public TaskCompletionSource<ConsoleContextSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Completion.Task;
    }

    private sealed class MutableContextProvider : IConsoleContextProvider
    {
        public required ConsoleContextSnapshot Snapshot { get; set; }
        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            return Snapshot;
        }
    }

    private sealed class CreationContext(ResourceScopeRef? scope) : IConsoleNavigationContext, IConsoleContextProvider
    {
        public string? ScopeRef { get; private set; } = scope?.Value;
        public string AddToUri(string uri) => uri;
        public void SetSelectedScopeRef(string? scopeRef) => ScopeRef = scopeRef;
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(SnapshotFor(scope ?? Scope));
    }

    private sealed class ApiHandler : HttpMessageHandler
    {
        private static readonly Guid DefinitionUid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public ToolResource Source { get; } = Tool("2.4.0");
        public ResourceScopeRef OwnerScope { get; set; } = Scope;
        public CreateToolDefinitionRequest? Created { get; private set; }
        public bool RejectNextCreate { get; set; }
        public List<(HttpMethod Method, string Path, string? Context)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, request.Headers.TryGetValues(ConsoleContextHeaders.Name, out var contexts) ? contexts.Single() : null));
            if (path == "/api/resource-scopes/targets") return Ok(new[]
            {
                new ResourceScopeTargetResponse(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", true),
                new ResourceScopeTargetResponse(ResourceScopeRef.Tenant(Guid.Parse("22222222-2222-2222-2222-222222222222")), ResourceScopeKind.Tenant, "Development", true),
                new ResourceScopeTargetResponse(Scope, ResourceScopeKind.Workspace, "Default workspace", true),
                new ResourceScopeTargetResponse(ResourceScopeRef.Workspace(Guid.Parse("33333333-3333-3333-3333-333333333333")), ResourceScopeKind.Workspace, "Read only", false)
            });
            if (path == "/api/flows") return Ok(new FlowPageResponse([new("source-flow", "Source Flow", null, FlowKind.Workflow, "2.4.0", true, "2.4.0", DateTimeOffset.UnixEpoch) { ScopeRef = OwnerScope }], null));
            if (path.EndsWith("/versions", StringComparison.Ordinal)) return Ok(new[] { Version("2.4.0"), Version("2.3.0") });
            if (path.Contains("/versions/", StringComparison.Ordinal)) return Ok(Version(path.Split('/')[^1]));
            if (path == "/api/tools") return Ok(new { value = new[] { Source with { ScopeRef = OwnerScope }, Tool("2.3.0") with { ScopeRef = OwnerScope } } });
            if (path is "/api/parameters" or "/api/secrets") return Ok(Array.Empty<object>());
            if (path == "/api/schemas") return Ok(new { value = Array.Empty<object>() });
            if (path == "/api/schemas/formats/validate-definition") return Ok(SchemaFormatValidationResult.Valid);
            if (path == "/api/tooldefinitions" && request.Method == HttpMethod.Post)
            {
                if (RejectNextCreate)
                {
                    RejectNextCreate = false;
                    return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { title = "Already exists", detail = "Already exists", status = 409 }) };
                }
                Created = await request.Content!.ReadFromJsonAsync<CreateToolDefinitionRequest>(cancellationToken);
                return Ok(new ToolDefinitionResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolDefinition, Metadata = new() { Name = Created!.Name, Namespace = ResourceNamespace.Parse(Created.Namespace) }, ScopeRef = Created.ScopeRef ?? OwnerScope, Definition = Created.Properties });
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }

        private static FlowVersionResponse Version(string version) => new("source-flow", version, null, new WorkflowFlowDefinition("input", [], [], []), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch,
            new() { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input", Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } } }) }, new OutputFlowStepDefinition { Name = "done", Outcome = FlowOutputOutcome.Success }] });
        private static ToolResource Tool(string version) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new() { Name = $"source.v{version}" },
            ScopeRef = Scope,
            Uid = Guid.NewGuid(),
            Generation = 1,
            Definition = new() { DisplayName = "Source Tool", Version = version, Enabled = true, Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }, Schema = new() { Input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { collection = new { type = "string" } } }) }, Metadata = new Dictionary<string, JsonElement> { ["agentstration.toolDefinitionUid"] = JsonSerializer.SerializeToElement(DefinitionUid) } }
        };
        private static HttpResponseMessage Ok<T>(T value)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
            response.Headers.ETag = new("\"1\"");
            return response;
        }
    }
}
