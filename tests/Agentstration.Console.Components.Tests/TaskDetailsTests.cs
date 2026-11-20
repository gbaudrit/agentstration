using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class TaskDetailsTests
{
    [TestMethod]
    public async Task InitialRenderHasNavigationIdentityWhileTheRealConsoleContextIsPending()
    {
        using var handler = new Handler(); var provider = new DeferredProvider(); using var context = Context(handler, provider: provider);
        var rendered = Render(context, handler);
        Assert.AreEqual(ResourceLoadStatus.Loading, rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(rendered.FindComponent<ResourceDetailsTemplate>().Instance.ResourceIdentity));
        Assert.AreEqual(0, handler.Reads);
        await rendered.InvokeAsync(async () => provider.Completion.SetResult(await new Provider(true).GetAsync(default)));
        rendered.WaitForElement("[data-task-section=overview]");
        Assert.AreEqual(1, handler.Reads);
    }

    [TestMethod]
    public async Task SectionsAndUnchangedRefreshKeepTheSameResponsePanelAndRawInput()
    {
        using var culture = new TestCultureScope("en-US"); using var handler = new Handler(); using var context = Context(handler);
        var rendered = Render(context, handler);
        rendered.WaitForElement("[data-task-section=overview] .metric-card");
        Assert.IsTrue(rendered.Find("[data-task-section=overview]").TextContent.Contains(handler.Id.ToString(), StringComparison.Ordinal));
        Assert.AreEqual(0, rendered.FindAll(".resource-header [role=switch], [data-resource-section=yaml], [data-resource-section=access], [data-resource-section='definition/identity']").Count);
        Section(context, "pending-actions");
        var panel = rendered.FindComponent<Agentstration.Workplace.Components.PendingActionPanel>().Instance;
        rendered.Find(".pending-action textarea").Input("raw response kept before blur");
        Section(context, "results"); Section(context, "pending-actions");
        Assert.AreSame(panel, rendered.FindComponent<Agentstration.Workplace.Components.PendingActionPanel>().Instance);
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        rendered.WaitForAssertion(() => Assert.AreEqual(2, handler.Reads));
        Assert.AreEqual("raw response kept before blur", rendered.Find(".pending-action textarea").GetAttribute("value"));
        Assert.IsTrue(rendered.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
        Assert.IsFalse(rendered.Markup.Contains("A newer task view", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChangedRefreshIsBufferedUntilExplicitDiscard()
    {
        using var culture = new TestCultureScope("en-US"); using var handler = new Handler(); using var context = Context(handler); var rendered = Render(context, handler);
        rendered.WaitForElement(".pending-action textarea"); rendered.Find(".pending-action textarea").Input("retained answer");
        handler.Title = "New saved task title";
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "A newer task view"));
        Assert.AreEqual("Original task", rendered.FindComponent<ResourceDetailsTemplate>().Instance.Name);
        rendered.Find(".unsaved-changes-bar button").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual(handler.Title, rendered.FindComponent<ResourceDetailsTemplate>().Instance.Name));
        Assert.AreEqual("", rendered.Find(".pending-action textarea").GetAttribute("value"));
        Assert.IsFalse(rendered.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClearingTheLastResponseAdoptsTheBufferedViewWithoutAnInaccessibleDiscardNotice(bool resolved)
    {
        using var culture = new TestCultureScope("en-US"); using var handler = new Handler(); using var context = Context(handler); var rendered = Render(context, handler);
        rendered.WaitForElement(".pending-action textarea"); rendered.Find(".pending-action textarea").Input("pending response");
        handler.Title = "Latest saved task"; handler.Resolved = resolved;
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "A newer task view"));
        rendered.Find(".pending-action textarea").Input("");
        rendered.WaitForAssertion(() => Assert.AreEqual(handler.Title, rendered.FindComponent<ResourceDetailsTemplate>().Instance.Name));
        Assert.IsFalse(rendered.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
        Assert.IsFalse(rendered.Markup.Contains("A newer task view", StringComparison.Ordinal));
        if (resolved) Assert.AreEqual(0, rendered.FindAll(".pending-action textarea").Count);
        else Assert.AreEqual("", rendered.Find(".pending-action textarea").GetAttribute("value"));
        Assert.AreEqual(2, handler.Reads);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, ResourceLoadStatus.Failed, true)]
    [DataRow(HttpStatusCode.Forbidden, ResourceLoadStatus.Forbidden, false)]
    [DataRow(HttpStatusCode.Unauthorized, ResourceLoadStatus.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, ResourceLoadStatus.NotFound, false)]
    public async Task FailedRefreshKeepsRecoverableDraftButClearsPrivateContentOnLostRead(HttpStatusCode code, ResourceLoadStatus state, bool retained)
    {
        using var handler = new Handler(); using var context = Context(handler); var rendered = Render(context, handler);
        rendered.WaitForElement(".pending-action textarea"); rendered.Find(".pending-action textarea").Input("private answer");
        handler.Failure = code;
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        rendered.WaitForAssertion(() => Assert.AreEqual(state, rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status));
        Assert.AreEqual(retained, rendered.FindAll(".pending-action textarea").Count > 0);
        Assert.AreEqual(retained, rendered.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
        if (retained) Assert.AreEqual("private answer", rendered.Find(".pending-action textarea").GetAttribute("value"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void WrongTaskOrUnauthorizedWorkspaceCannotExposeAnyContent(bool wrongTask)
    {
        using var handler = new Handler(); if (wrongTask) handler.ResponseId = Guid.NewGuid(); else handler.ResponseWorkspace = Guid.NewGuid();
        using var context = Context(handler); var rendered = Render(context, handler);
        rendered.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.Forbidden, rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status));
        Assert.AreEqual(0, rendered.FindAll("[data-task-section]").Count);
    }

    [TestMethod]
    public async Task ActualResponseUsesExactTaskAndActionAndDoesNotRetryAnUnconfirmedWrite()
    {
        using var culture = new TestCultureScope("en-US"); using var handler = new Handler { WriteFailure = true }; using var context = Context(handler); var rendered = Render(context, handler);
        rendered.WaitForElement(".pending-action textarea"); rendered.Find(".pending-action textarea").Input("unconfirmed response");
        await rendered.Find(".pending-action .action-button").ClickAsync(new());
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Find("[role=alert]").TextContent, "could not be confirmed"));
        Assert.AreEqual($"/api/tasks/{handler.Id}/pending-actions/{handler.ActionId}/respond", handler.WritePath);
        Assert.AreEqual("unconfirmed response", handler.Answer);
        Assert.AreEqual("unconfirmed response", rendered.Find(".pending-action textarea").GetAttribute("value"));
        Assert.AreEqual(1, handler.Reads); Assert.AreEqual(1, handler.Writes);
        handler.WriteFailure = false; await rendered.Find(".pending-action .action-button").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.AreEqual(0, rendered.FindAll(".pending-action textarea").Count));
        Assert.IsFalse(rendered.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
    }

    [TestMethod]
    public async Task PauseResumeAndConfirmedCancellationKeepUnrelatedResponseAndUseExactCommands()
    {
        using var culture = new TestCultureScope("en-US"); using var handler = new Handler(); using var context = Context(handler); var rendered = Render(context, handler);
        rendered.WaitForElement(".pending-action textarea"); rendered.Find(".pending-action textarea").Input("response retained through operational commands");
        await rendered.FindAll(".resource-header button").Single(value => value.TextContent == "Pause").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.AreEqual("/api/tasks/" + handler.Id + "/pause", handler.WritePath));
        await rendered.FindAll(".resource-header button").Single(value => value.TextContent == "Resume").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.AreEqual("/api/tasks/" + handler.Id + "/resume", handler.WritePath));
        rendered.FindAll(".resource-header button").Single(value => value.TextContent == "Cancel").Click();
        var confirmation = rendered.FindComponents<ConfirmationDialog>().Single(value => value.Instance.IsOpen);
        await rendered.InvokeAsync(() => confirmation.Instance.OnCancel.InvokeAsync());
        Assert.AreEqual(2, handler.Writes);
        rendered.FindAll(".resource-header button").Single(value => value.TextContent == "Cancel").Click();
        confirmation = rendered.FindComponents<ConfirmationDialog>().Single(value => value.Instance.IsOpen);
        await rendered.InvokeAsync(() => confirmation.Instance.OnConfirm.InvokeAsync());
        rendered.WaitForAssertion(() => Assert.AreEqual("/api/tasks/" + handler.Id + "/cancel", handler.WritePath));
        Assert.AreEqual(3, handler.Writes);
        Assert.AreEqual("response retained through operational commands", rendered.Find(".pending-action textarea").GetAttribute("value"));
    }

    [TestMethod]
    public async Task DeniedExecutionDoesNotRevokeIndependentReadOrDiscardTheResponse()
    {
        using var handler = new Handler { WriteStatus = HttpStatusCode.Forbidden }; using var context = Context(handler); var rendered = Render(context, handler);
        rendered.WaitForElement(".pending-action textarea"); rendered.Find(".pending-action textarea").Input("retained after denied execution");
        await rendered.Find(".pending-action .action-button").ClickAsync(new());
        rendered.WaitForElement("[role=alert]");
        Assert.AreEqual(ResourceLoadStatus.Ready, rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.AreEqual("retained after denied execution", rendered.Find(".pending-action textarea").GetAttribute("value"));
        Assert.IsTrue(rendered.FindComponent<ResourceDetailsTemplate>().Instance.HasDraft);
        Assert.AreEqual(1, handler.Reads);
    }

    [TestMethod]
    public void ReadPermissionDoesNotGrantExecutionCommandsOrAnEditableResponse()
    {
        using var culture = new TestCultureScope("en-US"); using var handler = new Handler(); using var context = Context(handler, false); var rendered = Render(context, handler);
        rendered.WaitForElement("[data-task-section=overview]");
        Assert.AreEqual(0, rendered.FindAll(".resource-header button").Count(button => button.TextContent is "Pause" or "Resume" or "Cancel"));
        Assert.AreEqual(0, rendered.FindAll(".pending-action").Count);
        StringAssert.Contains(rendered.Markup, "runs/execute permission is required");
    }

    [TestMethod]
    public async Task InputDuringPendingReadIsRetainedAndRealtimeForOtherTasksDoesNotReload()
    {
        using var handler = new Handler(); using var context = Context(handler); var rendered = Render(context, handler); rendered.WaitForElement(".pending-action textarea");
        var realtime = (Realtime)context.Services.GetRequiredService<IWorkOperationsRealtimeClient>();
        await realtime.Emit(new(Guid.NewGuid())); Assert.AreEqual(1, handler.Reads);
        var completion = new TaskCompletionSource<HttpResponseMessage>(); handler.Pending = completion.Task;
        var refresh = rendered.InvokeAsync(() => rendered.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        rendered.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.Refreshing, rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status));
        Assert.IsFalse(rendered.Find(".pending-action textarea").HasAttribute("disabled"));
        Assert.IsTrue(rendered.Find(".pending-action .action-button").HasAttribute("disabled"));
        await realtime.Emit(new(handler.Id));
        rendered.Find(".pending-action textarea").Input("typed during read"); handler.Title = "Changed during read";
        completion.SetResult(handler.Response()); await refresh;
        rendered.WaitForAssertion(() => Assert.AreEqual("typed during read", rendered.Find(".pending-action textarea").GetAttribute("value")));
        Assert.AreEqual("Original task", rendered.FindComponent<ResourceDetailsTemplate>().Instance.Name);
        Assert.AreEqual(3, handler.Reads);
    }

    private static IRenderedComponent<TaskDetails> Render(BunitContext context, Handler handler) => context.Render<TaskDetails>(p => p.Add(value => value.TaskId, handler.Id));
    private static void Section(BunitContext context, string path) { var nav = context.Services.GetRequiredService<NavigationManager>(); nav.NavigateTo(nav.Uri.Split('#')[0] + "#" + path); }
    private static BunitContext Context(Handler handler, bool execute = true, IConsoleContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js"); module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult("http://localhost/#overview");
        module.Setup<string>("navigate", _ => true).SetResult("http://localhost/#overview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IWorkApiClient>(new WorkApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost/") }));
        context.Services.AddSingleton<IWorkOperationsRealtimeClient>(new Realtime());
        context.Services.AddSingleton(new ConsoleContextState(provider ?? new Provider(execute)));
        context.Services.AddSingleton(TimeProvider.System); context.Services.Configure<AgentstrationWebOptions>(options => options.WorkApi.BaseAddress = "http://work.local/");
        return context;
    }
    private sealed class DeferredProvider : IConsoleContextProvider
    {
        public TaskCompletionSource<ConsoleContextSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken token) => Completion.Task;
    }
    private sealed class Provider(bool execute) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken token) => Task.FromResult(new ConsoleContextSnapshot(Guid.NewGuid(), "Operator", Guid.NewGuid(), "dev", "Development", Handler.Workspace, "default", "Default workspace", new HashSet<string>(execute ? [AuthorizationPermissions.RunsRead, AuthorizationPermissions.RunsExecute] : [AuthorizationPermissions.RunsRead]), []));
    }
    private sealed class Realtime : IWorkOperationsRealtimeClient
    {
        public WorkOperationsRealtimeState State => WorkOperationsRealtimeState.Offline;
        public event Func<WorkOperationsRealtimeUpdate, Task>? Updated;
        public event Action? StateChanged { add { } remove { } }
        public Task StartAsync(IEnumerable<string> ids, CancellationToken token) => Task.CompletedTask;
        public Task Emit(WorkOperationsRealtimeUpdate update) => Updated?.Invoke(update) ?? Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public static readonly Guid Workspace = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public Guid Id { get; } = Guid.NewGuid(); public Guid ActionId { get; } = Guid.NewGuid();
        public Guid? ResponseId { get; set; } public Guid ResponseWorkspace { get; set; } = Workspace;
        public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Running; public string Title { get; set; } = "Original task"; public HttpStatusCode? Failure { get; set; }
        public HttpStatusCode? WriteStatus { get; set; } public bool WriteFailure { get; set; } public bool Resolved { get; set; }
        public string? WritePath { get; private set; } public string? Answer { get; private set; }
        public int Reads { get; private set; } public int Writes { get; private set; }
        public Task<HttpResponseMessage>? Pending { get; set; }
        private PendingActionContract Action => new(ActionId, ResponseWorkspace, null, ResponseId ?? Id, null, PendingActionKind.InputRequired, Resolved ? PendingActionStatus.Completed : PendingActionStatus.Pending, "Response required", null, [new() { Name = "answer", Type = EntryFieldType.Textarea }], DateTimeOffset.UnixEpoch, null, null, 1);
        public HttpResponseMessage Response()
        {
            var task = new WorkTaskOperationsSummary(ResponseId ?? Id, ResponseWorkspace.ToString("D"), null, null, Title, "Saved description", Status, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null, Resolved ? 0 : 1, 0, 0, 0, null, null);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkTaskOperationsDetailResponse(task, null, [Action], [], [], [], [], [])) };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) { ++Reads; if (Pending is { } pending) { Pending = null; return await pending; } if (Failure is { } code) return new(code) { Content = JsonContent.Create(new { title = "Read unavailable" }) }; return Response(); }
            ++Writes; WritePath = request.RequestUri!.AbsolutePath;
            if (WriteStatus is { } denied) return new(denied) { Content = JsonContent.Create(new { title = "Execution denied" }) };
            if (WritePath.EndsWith("/respond", StringComparison.Ordinal)) { using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Answer = input.RootElement.GetProperty("values").GetProperty("answer").GetString(); if (WriteFailure) throw new HttpRequestException("Offline"); Resolved = true; return new(HttpStatusCode.OK) { Content = JsonContent.Create(Action) }; }
            Status = WritePath.EndsWith("/pause", StringComparison.Ordinal) ? WorkTaskStatus.Paused : WritePath.EndsWith("/resume", StringComparison.Ordinal) ? WorkTaskStatus.Running : WorkTaskStatus.Cancelled;
            return new(HttpStatusCode.NoContent);
        }
    }
}
