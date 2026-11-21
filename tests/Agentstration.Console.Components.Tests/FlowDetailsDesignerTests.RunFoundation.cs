using System.Net;
using Agentstration.Flows;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class FlowDetailsDesignerTests
{
    [TestMethod]
    public async Task RunRefreshFailureAndRetryRetainTheResponseAndVisitedInspection()
    {
        using var context = RunLayoutContext();
        var run = LayoutRun("private-refresh-execution") with { Status = FlowRunStatus.WaitingForInput };
        Exception? failure = null;
        var input = PendingRunInput(run);
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub { RunLoader = _ => failure is null ? Task.FromResult(run) : Task.FromException<FlowRun>(failure), PendingInputs = [input] });
        var cut = context.Render<FlowRunDetails>(p => p.Add(value => value.RunId, run.Id));
        cut.WaitForElement(".input-request-panel input").Input("Unsent response");
        var adapter = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        SelectFlowSection(context, "input-output");
        var topology = cut.FindComponent<FlowTopologyViewer>().Instance;
        var state = adapter.SectionState;
        failure = new AgentstrationApiException("Unavailable", "fixture", HttpStatusCode.ServiceUnavailable);

        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());

        Assert.AreEqual(ResourceLoadStatus.Failed, adapter.LoadState!.Status);
        Assert.IsTrue(adapter.LoadState!.HasContent);
        Assert.AreSame(topology, cut.FindComponent<FlowTopologyViewer>().Instance);
        Assert.AreSame(state, adapter.SectionState);
        Assert.AreEqual("input-output", state!.CurrentPath);
        Assert.AreEqual("Unsent response", cut.Find(".input-request-panel input").GetAttribute("value"));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        failure = null;
        await cut.InvokeAsync(() => adapter.OnRetry.InvokeAsync());
        Assert.AreEqual(ResourceLoadStatus.Ready, adapter.LoadState!.Status);
        Assert.AreEqual("Unsent response", cut.Find(".input-request-panel input").GetAttribute("value"));
        Assert.AreSame(topology, cut.FindComponent<FlowTopologyViewer>().Instance);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task LosingRunAccessClearsTheResponseAndCachedExecution(HttpStatusCode status)
    {
        using var context = RunLayoutContext();
        var run = LayoutRun("private-denied-execution") with { Status = FlowRunStatus.WaitingForInput };
        Exception? failure = null;
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub { RunLoader = _ => failure is null ? Task.FromResult(run) : Task.FromException<FlowRun>(failure), PendingInputs = [PendingRunInput(run)] });
        var cut = context.Render<FlowRunDetails>(p => p.Add(value => value.RunId, run.Id));
        cut.WaitForElement(".input-request-panel input").Input("Unsent private response");
        var adapter = cut.FindComponent<ResourceDetailsTemplate>().Instance;
        SelectFlowSection(context, "input-output");
        failure = new AgentstrationApiException("Denied", "fixture", status);

        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());

        Assert.AreEqual(status == HttpStatusCode.NotFound ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Forbidden, adapter.LoadState!.Status);
        Assert.IsFalse(adapter.LoadState!.HasContent);
        Assert.IsEmpty(cut.FindAll(".input-request-panel, .flow-run-overview, .io-panel"));
        Assert.DoesNotContain("Private input request", cut.Markup);
        Assert.DoesNotContain("Unsent private response", cut.Markup);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task AnInitiallyUnavailableRunCanRetryWithoutChangingItsAddress()
    {
        using var context = RunLayoutContext();
        var run = LayoutRun("retry-execution");
        var unavailable = true;
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub
        {
            RunLoader = _ => unavailable
            ? Task.FromException<FlowRun>(new AgentstrationApiException("Unavailable", "fixture", HttpStatusCode.ServiceUnavailable)) : Task.FromResult(run)
        });
        var cut = context.Render<FlowRunDetails>(p => p.Add(value => value.RunId, run.Id));
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.IsFalse(cut.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.HasContent);
        unavailable = false;
        await cut.InvokeAsync(() => cut.FindComponent<ResourceDetailsTemplate>().Instance.OnRetry.InvokeAsync());
        Assert.AreEqual(ResourceLoadStatus.Ready, cut.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.Contains("team/recorded-definition", cut.Find(".resource-details").TextContent);
    }

    private static InputRequest PendingRunInput(FlowRun run) => new()
    {
        Id = "pending",
        RunId = run.Id,
        RuntimeRequestId = "runtime-input",
        ScopeRef = run.ExecutionScopeRef,
        Prompt = "Private input request",
        CreatedAt = FlowClientStub.Now
    };
}
