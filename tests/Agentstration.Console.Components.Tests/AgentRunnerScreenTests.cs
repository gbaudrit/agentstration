using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Models.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
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
public sealed class AgentRunnerScreenTests
{
    [TestMethod]
    public void RequestInputsAndInvalidNumberRemainMountedAcrossSections()
    {
        using var handler = new Handler(); using var context = Context(handler); var rendered = NewRunner(context);
        rendered.WaitForElement("[data-testid=agent-run-prompt]").Change("Pending prompt");
        rendered.Find("form .text-button").Click();
        rendered.Find("form input[type=number]").Change("invalid");
        var form = rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance.EditContext;
        Select(context, "overview"); Select(context, "run");
        Assert.AreSame(form, rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance.EditContext);
        Assert.AreEqual("Pending prompt", rendered.Find("[data-testid=agent-run-prompt]").GetAttribute("value"));
        Assert.AreEqual("invalid", rendered.Find("form input[type=number]").GetAttribute("value"));
        Assert.IsTrue(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
        Assert.IsTrue(rendered.Find("[data-testid=agent-run-submit]").HasAttribute("disabled"));
        Assert.AreEqual(1, handler.AgentReads);
        Assert.AreEqual(Owner.Value, handler.ModelOwner);
        Assert.AreEqual(Owner.Value, handler.ReadinessOwner);
    }

    [TestMethod]
    public void LateDefaultContextNormalizationKeepsTheRequestAndItsEditContext()
    {
        using var handler = new Handler(); var navigationContext = new NavigationContext();
        using var context = Context(handler, navigationContext: navigationContext); var rendered = NewRunner(context);
        rendered.WaitForElement("[data-testid=agent-run-prompt]").Change("Keep this pending request");
        var form = rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance.EditContext;
        navigationContext.ScopeRef = Execution.Value;
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/agents/runner/run?namespace=team&scopeRef=" + Uri.EscapeDataString(Owner.Value) + "&context=" + Uri.EscapeDataString(Execution.Value) + "#run");
        rendered.Render();
        Assert.AreSame(form, rendered.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance.EditContext);
        Assert.AreEqual("Keep this pending request", rendered.Find("[data-testid=agent-run-prompt]").GetAttribute("value"));
        Assert.AreEqual(1, handler.AgentReads);
        Assert.IsTrue(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
    }

    [TestMethod]
    public void CreatingARunKeepsTheExactAgentReferenceAndLeavesExecutionScopeInferenceToTheApi()
    {
        using var handler = new Handler(); var navigationContext = new NavigationContext();
        using var context = Context(handler, navigationContext: navigationContext); var rendered = NewRunner(context);
        rendered.WaitForElement("[data-testid=agent-run-prompt]").Change("Deterministic fixture request");
        rendered.Find("form").Submit();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Created));
        Assert.AreEqual(Owner, handler.Created!.Agent.ScopeRef);
        Assert.AreEqual(new ResourceNamespace("team"), handler.Created.Agent.Namespace);
        Assert.AreEqual(handler.Agent.Uid, handler.Created.Agent.ResourceUid);
        Assert.AreEqual(7, handler.Created.Agent.Version);
        Assert.IsNull(handler.Created.ExecutionScopeRef);
        Assert.IsFalse(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
        rendered.WaitForAssertion(() => Assert.AreEqual("execution/response", rendered.FindComponent<ResourceNavigation>().Instance.CurrentPath));
        navigationContext.ScopeRef = Execution.Value;
        rendered.Render();
        Assert.AreEqual("created", rendered.FindComponent<ResourceDetailsTemplate>().Instance.TechnicalName);
        Assert.AreEqual(1, handler.AgentReads);
    }

    [TestMethod]
    public void RecordedOverviewKeepsHistoricalGenerationAndModelFactsSeparateFromTheCurrentAgent()
    {
        using var handler = new Handler(); using var context = Context(handler);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/runs/recorded?scopeRef=" + Uri.EscapeDataString(Execution.Value) + "#overview");
        var rendered = context.Render<AgentRunner>(p => p.Add(value => value.RunId, "recorded"));
        rendered.WaitForElement(".resource-identity-summary");
        var template = rendered.FindComponent<ResourceDetailsTemplate>().Instance;
        Assert.IsNull(template.ScopeRef);
        Assert.IsNull(template.Namespace);
        Assert.IsNull(template.Enabled);
        StringAssert.Contains(rendered.Find(".resource-configuration-summary").TextContent, "recorded-profile");
        StringAssert.Contains(rendered.Find(".resource-configuration-summary").TextContent, "recorded-model");
        Assert.DoesNotContain("current-profile", rendered.Find(".resource-configuration-summary").TextContent);
        Assert.AreEqual(2, handler.Stored.Properties.Agent.Version);
        Assert.AreEqual(7, handler.Agent.Generation);
        Select(context, "history");
        StringAssert.Contains(rendered.Markup, "team/runner");
        Assert.AreEqual(1, handler.RunReads);
    }

    [TestMethod]
    public void ReadOnlyActorsCannotRunOrPrepareEvenWhenTheModelResolves()
    {
        using var handler = new Handler(); using var context = Context(handler, canWrite: false); var rendered = NewRunner(context);
        rendered.WaitForElement("[data-testid=agent-run-prompt]").Change("A valid fixture prompt");
        Assert.IsTrue(rendered.Find("[data-testid=agent-run-submit]").HasAttribute("disabled"));
        Assert.IsTrue(rendered.Find(".resource-header .resource-action-bar button.button-secondary").HasAttribute("disabled"));
        rendered.Find("form").Submit();
        Assert.IsNull(handler.Created);
        Assert.AreEqual(0, handler.Prepares);
    }

    [TestMethod]
    public async Task AnOldAgentReplyCannotReplaceTheNextResource()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(); handler.AgentLoader = name => name == "runner" ? pending.Task : Task.FromResult(handler.Reply(handler.Agent with { Metadata = handler.Agent.Metadata with { Name = name } }));
        using var context = Context(handler); var rendered = NewRunner(context);
        rendered.Render(p => p.Add(value => value.Name, "other"));
        rendered.WaitForElement("[data-testid=agent-run-prompt]");
        await rendered.InvokeAsync(() => pending.SetResult(handler.Reply(handler.Agent)));
        Assert.AreEqual("other", rendered.FindComponent<ResourceDetailsTemplate>().Instance.TechnicalName);
    }

    private static readonly ResourceScopeRef Owner = ResourceScopeRef.Instance;
    private static readonly ResourceScopeRef Execution = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static IRenderedComponent<AgentRunner> NewRunner(BunitContext context) => context.Render<AgentRunner>(p => p.Add(value => value.Name, "runner"));
    private static void Select(BunitContext context, string path) { var navigation = context.Services.GetRequiredService<NavigationManager>(); navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#" + path); }
    private static BunitContext Context(Handler handler, bool canWrite = true, NavigationContext? navigationContext = null)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js"); module.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        if (navigationContext is not null) context.Services.AddSingleton<IConsoleNavigationContext>(navigationContext);
        context.Services.AddSingleton<IAgentRunnerManagementClient>(new ManagementApiClient(http));
        context.Services.AddSingleton<IAgentRunnerRuntimeClient>(new RuntimeApiClient(http));
        context.Services.AddSingleton<IAgentsModelClient>(new AgentsModelApiClient(http));
        context.Services.AddSingleton<IConsoleContextProvider>(new ContextProvider(canWrite)); context.Services.AddScoped<ConsoleContextState>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/agents/runner/run?namespace=team&scopeRef=" + Uri.EscapeDataString(Owner.Value) + "#run");
        return context;
    }
    private sealed class NavigationContext : IConsoleNavigationContext
    {
        public string? ScopeRef { get; set; }
        public string AddToUri(string uri) => ScopeRef is null ? uri : QueryHelpers.AddQueryString(uri, "context", ScopeRef);
    }
    private sealed class ContextProvider(bool canWrite) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(Guid.NewGuid(), "Fixture actor", Guid.NewGuid(), "fixture", "Fixture tenant", Execution.TargetId!.Value, "fixture", "Fixture workspace",
            canWrite ? new HashSet<string> { "resources/read", "resources/write", "runs/read", "runs/execute" } : new HashSet<string> { "resources/read", "runs/read" }, []));
    }
    private sealed class Handler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public AgentResource Agent { get; } = new() { Uid = Guid.NewGuid(), ApiVersion = ResourceApiVersions.CoreV1, Kind = AgentResourceKinds.Agent, Metadata = new() { Name = "runner", Namespace = new("team") }, ScopeRef = Owner, Generation = 7,
            Definition = new() { DisplayName = "Fixture Agent", Description = "Current definition", Instructions = "Respond deterministically", ModelProfile = new("current-profile", Owner), RuntimeProfile = new("maf-builtin", Owner) } };
        public RuntimeRun Stored => Run("recorded", new("runner", 2) { Namespace = new("team"), ScopeRef = Owner, ResourceUid = Agent.Uid });
        public CreateRuntimeRunRequest? Created { get; private set; }
        public int AgentReads { get; private set; }
        public int RunReads { get; private set; }
        public int Prepares { get; private set; }
        public string? ModelOwner { get; private set; }
        public string? ReadinessOwner { get; private set; }
        public Func<string, Task<HttpResponseMessage>>? AgentLoader { get; set; }
        public HttpResponseMessage Reply<T>(T value) { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) }; response.Headers.ETag = new("\"fixture-etag\""); return response; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath; var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (path.EndsWith("/model", StringComparison.Ordinal)) { ModelOwner = query["scopeRef"].ToString(); return Reply(new AgentModelResponse(new(new("current-profile", "current-profile")), new(null, new("current-model"), new(new(), new(), new())), "ready", [])); }
            if (path.EndsWith("/readiness", StringComparison.Ordinal)) { ReadinessOwner = query["scopeRef"].ToString(); return Reply(new AgentRuntimeReadinessResponse("runner", 7, true, "Ready", null, null, null)); }
            if (path.EndsWith("/prepare", StringComparison.Ordinal)) { Prepares++; return Reply(new PrepareAgentRuntimeResponse("runner", 7, "deployment", "revision", "Ready")); }
            if (path.EndsWith("/events", StringComparison.Ordinal)) return new(HttpStatusCode.OK) { Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/event-stream") };
            if (path == "/api/runtime/runs" && request.Method == HttpMethod.Post) { Created = await request.Content!.ReadFromJsonAsync<CreateRuntimeRunRequest>(Json, cancellationToken); return Reply(Run("created", Created!.Agent)); }
            if (path == "/api/runtime/runs") return Reply(new RuntimeRunPageResponse([Stored]));
            if (path == "/api/runtime/runs/recorded") { RunReads++; return Reply(Stored); }
            if (path.Contains("/agents/", StringComparison.Ordinal)) { AgentReads++; var name = path.Split('/').Last(); return AgentLoader is not null ? await AgentLoader(name) : Reply(Agent with { Metadata = Agent.Metadata with { Name = name } }); }
            throw new InvalidOperationException("Unexpected fixture request: " + request.Method + " " + request.RequestUri);
        }
        private static RuntimeRun Run(string id, RuntimeAgentReference reference) => new() { Id = id, Name = id, WorkspaceId = new(Execution.TargetId!.Value), Scope = RuntimeRunScope.Workspace(Guid.NewGuid(), new(Execution.TargetId.Value), Guid.NewGuid()),
            Properties = new() { Agent = reference, Input = new() { Messages = [new(RuntimeMessageRole.User, "Recorded request")] }, Execution = new(), Origin = RuntimeRunOrigin.Console, Initiator = "Fixture actor" },
            Status = new() { State = RuntimeRunState.Succeeded, CreatedAt = DateTimeOffset.UnixEpoch, Response = "Recorded response", ModelProfile = "recorded-profile", ModelProvider = "recorded-provider", ResolvedModel = "recorded-model" } };
    }
}
