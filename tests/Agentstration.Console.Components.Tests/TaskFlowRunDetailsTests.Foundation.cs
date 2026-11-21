using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class TaskFlowRunDetailsTests
{
    [TestMethod]
    public async Task RecoverableRefreshKeepsTheInspectionAndSelectedSectionThroughRetry()
    {
        using var handler = new RunHandler(); using var http = Http(handler); using var context = Context(http);
        var cut = RenderRun(context, handler, Guid.NewGuid());
        await OpenExecutionAsync(context, cut);
        var topology = cut.FindComponent<FlowTopologyViewer>().Instance;
        handler.Status = HttpStatusCode.ServiceUnavailable;

        await cut.InvokeAsync(() => cut.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());

        Assert.AreEqual(ResourceLoadStatus.Failed, State(cut).Status);
        Assert.IsTrue(State(cut).HasContent);
        Assert.IsFalse(cut.Find("[data-task-run-section=execution]").HasAttribute("hidden"));
        StringAssert.Contains(cut.Find(".io-panel").TextContent, "kept-input");
        Assert.AreSame(topology, cut.FindComponent<FlowTopologyViewer>().Instance);
        handler.Status = HttpStatusCode.OK;
        await cut.InvokeAsync(() => cut.FindComponent<ResourceDetailsTemplate>().Instance.OnRetry.InvokeAsync());
        Assert.AreEqual(ResourceLoadStatus.Ready, State(cut).Status);
        Assert.AreSame(topology, cut.FindComponent<FlowTopologyViewer>().Instance);
        Assert.HasCount(3, handler.Requests);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task AccessLossAndMissingRunsClearThePrivateInspection(HttpStatusCode status)
    {
        using var handler = new RunHandler(); using var http = Http(handler); using var context = Context(http);
        var cut = RenderRun(context, handler, Guid.NewGuid());
        await OpenExecutionAsync(context, cut);
        handler.Status = status;

        await cut.InvokeAsync(() => cut.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());

        Assert.AreEqual(status == HttpStatusCode.NotFound ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Forbidden, State(cut).Status);
        Assert.IsTrue(State(cut).BlocksContent);
        Assert.IsFalse(State(cut).CanRetry);
        Assert.IsEmpty(cut.FindAll("[data-task-run-section]"));
        Assert.DoesNotContain("kept-input", cut.Markup);
        Assert.IsEmpty(cut.FindComponents<FlowTopologyViewer>());
    }

    [TestMethod]
    public async Task AnInitialFailureCanRetryWithoutInventingResourceCapabilities()
    {
        using var handler = new RunHandler { Status = HttpStatusCode.ServiceUnavailable };
        using var http = Http(handler); using var context = Context(http);
        var cut = RenderRun(context, handler, Guid.NewGuid());
        Assert.AreEqual(ResourceLoadStatus.Failed, State(cut).Status);
        handler.Status = HttpStatusCode.OK;

        await cut.InvokeAsync(() => cut.FindComponent<ResourceDetailsTemplate>().Instance.OnRetry.InvokeAsync());

        Assert.AreEqual(ResourceLoadStatus.Ready, State(cut).Status);
        Assert.IsEmpty(cut.FindAll(".resource-header [role=switch]"));
        Assert.IsFalse(cut.FindComponent<ResourceDetailsTemplate>().Instance.Sections.Any(item => item.Path.StartsWith("definition", StringComparison.Ordinal) || item.Path is "yaml" or "access"));
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public void ARunForAnotherTaskCannotPopulateTheInspection()
    {
        using var handler = new RunHandler(); handler.Run = handler.Run with { WorkTaskId = Guid.NewGuid().ToString("D") };
        using var http = Http(handler); using var context = Context(http);
        var cut = RenderRun(context, handler, Guid.NewGuid());
        Assert.AreEqual(ResourceLoadStatus.NotFound, State(cut).Status);
        Assert.IsEmpty(cut.FindAll("[data-task-run-section]"));
        Assert.DoesNotContain("kept-input", cut.Markup);
    }

    [TestMethod]
    public async Task AChangedExecutionScopeCannotReplaceTheSavedRun()
    {
        using var handler = new RunHandler(); using var http = Http(handler); using var context = Context(http);
        var cut = RenderRun(context, handler, Guid.NewGuid());
        handler.Run = handler.Run with { Scope = new(Guid.NewGuid(), Agentstration.Resources.WorkspaceId.New(), Guid.NewGuid()) };

        await cut.InvokeAsync(() => cut.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());

        Assert.AreEqual(ResourceLoadStatus.NotFound, State(cut).Status);
        Assert.IsEmpty(cut.FindAll("[data-task-run-section]"));
    }

    [TestMethod]
    public async Task ADelayedOldTaskReadCannotReplaceTheNewTaskRun()
    {
        using var handler = new RunHandler(); using var http = Http(handler); using var context = Context(http);
        var oldTask = Guid.NewGuid(); var cut = RenderRun(context, handler, oldTask);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.NextRead = pending;
        var refresh = cut.InvokeAsync(() => cut.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.IsTrue(handler.ReadPending));
        handler.Run = handler.Run with { Id = "current-task-run", Input = JsonSerializer.SerializeToElement(new { request = "current-private-input" }) };
        var newTask = Guid.NewGuid();
        cut.Render(parameters => parameters.Add(component => component.TaskId, newTask).Add(component => component.RunId, handler.Run.Id));
        cut.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.Ready, State(cut).Status));
        pending.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(handler.Run with { Id = "task-flow-run", WorkTaskId = oldTask.ToString("D"), Input = JsonSerializer.SerializeToElement(new { request = "obsolete-private-input" }) }) });
        await refresh;
        await OpenExecutionAsync(context, cut);
        StringAssert.Contains(cut.Find(".io-panel").TextContent, "current-private-input");
        Assert.DoesNotContain("obsolete-private-input", cut.Markup);
        Assert.AreEqual(ResourceLoadStatus.Ready, State(cut).Status);
    }

    private static HttpClient Http(RunHandler handler) => new(handler) { BaseAddress = new("http://localhost/") };
    private static ResourceLoadState State(IRenderedComponent<TaskFlowRunDetails> cut) => cut.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!;
    private static IRenderedComponent<TaskFlowRunDetails> RenderRun(BunitContext context, RunHandler handler, Guid task)
    {
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/tasks/{task:D}/flowruns/{handler.Run.Id}");
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult(nav.Uri + "#overview");
        return context.Render<TaskFlowRunDetails>(parameters => parameters.Add(component => component.TaskId, task).Add(component => component.RunId, handler.Run.Id));
    }
    private static async Task OpenExecutionAsync(BunitContext context, IRenderedComponent<TaskFlowRunDetails> cut)
    {
        var nav = context.Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => nav.NavigateTo(nav.Uri.Split('#')[0] + "#execution"));
        cut.WaitForElement("[data-task-run-section=execution]");
    }
}
