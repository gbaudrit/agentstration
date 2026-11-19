using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Flows;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class FlowDetailsDesignerTests
{
    [TestMethod]
    public void RunSectionsRetainTheTopologyAndAvoidReloadingTheExecutionForFragments()
    {
        using var context = RunLayoutContext();
        var run = LayoutRun("retained-execution"); var client = new FlowClientStub(run: run);
        context.Services.AddSingleton<IFlowApiClient>(client);
        var rendered = context.Render<FlowRunDetails>(p => p.Add(value => value.RunId, run.Id));
        var topology = rendered.FindComponent<FlowTopologyViewer>().Instance;
        var identity = rendered.FindAll(".form-section").First();
        StringAssert.Contains(identity.TextContent, run.Id);
        Assert.AreEqual(run.ExecutionScopeRef.Value, rendered.Find("[data-testid=flow-run-execution-scope] .configuration-value").TextContent);
        SelectFlowSection(context, "raw-events");
        SelectFlowSection(context, "overview");
        Assert.AreSame(topology, rendered.FindComponent<FlowTopologyViewer>().Instance);
        rendered.Render();
        Assert.AreEqual(1, client.RunReadCount);
        Assert.IsNull(rendered.FindComponent<ResourceDetailsTemplate>().Instance.Enabled);
        Assert.IsNull(rendered.FindComponent<ResourceDetailsTemplate>().Instance.ScopeRef);
    }

    [TestMethod]
    public async Task ADelayedExecutionReplyCannotReplaceAnotherRun()
    {
        using var context = RunLayoutContext();
        var delayed = new TaskCompletionSource<FlowRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = LayoutRun("first-execution"); var second = LayoutRun("second-execution");
        var client = new FlowClientStub { RunLoader = id => id == first.Id ? delayed.Task : Task.FromResult(second) };
        context.Services.AddSingleton<IFlowApiClient>(client);
        var rendered = context.Render<FlowRunDetails>(p => p.Add(value => value.RunId, first.Id));
        rendered.Render(p => p.Add(value => value.RunId, second.Id));
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.FindComponent<ResourceDetailsTemplate>().Instance.Description!, second.Id));
        await rendered.InvokeAsync(() => delayed.SetResult(first));
        Assert.AreEqual(second.Id, rendered.FindComponent<ResourceDetailsTemplate>().Instance.Description);
        Assert.DoesNotContain(first.Id, rendered.Markup);
    }

    [TestMethod]
    public void AnUnexpectedExecutionIdentityNeverRendersTheOtherRunsDetails()
    {
        using var context = RunLayoutContext();
        var other = LayoutRun("other-execution");
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub { RunLoader = _ => Task.FromResult(other) });
        var rendered = context.Render<FlowRunDetails>(p => p.Add(value => value.RunId, "requested-execution"));
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindComponents<ErrorState>()));
        Assert.IsFalse(rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.HasContent);
        Assert.IsEmpty(rendered.FindAll(".resource-form, .flow-run-overview, .input-request-panel"));
        Assert.DoesNotContain(other.Id, rendered.Markup);
    }

    [TestMethod]
    public void AResponseDraftSurvivesSectionNavigationAndRemainsProtectedWhenLeavingTheRun()
    {
        using var context = RunLayoutContext();
        var run = LayoutRun("waiting-execution") with { Status = FlowRunStatus.WaitingForInput };
        var input = new InputRequest { Id = "pending", RunId = run.Id, RuntimeRequestId = "runtime-input", ScopeRef = run.ExecutionScopeRef, Prompt = "Fixture input request", CreatedAt = FlowClientStub.Now };
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub(run: run) { PendingInputs = [input] });
        var rendered = context.Render<FlowRunDetails>(p => p.Add(value => value.RunId, run.Id));
        rendered.WaitForElement(".input-request-panel input").Input("Pending response");
        SelectFlowSection(context, "raw-events"); SelectFlowSection(context, "overview");
        Assert.AreEqual("Pending response", rendered.Find(".input-request-panel input").GetAttribute("value"));
        Assert.IsTrue(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
    }

    [TestMethod]
    public async Task AReplayCompletedDuringAnotherRunsInitialLoadCannotTerminateTheCircuit()
    {
        using var context = RunLayoutContext();
        var pendingReplay = new TaskCompletionSource<IReadOnlyList<FlowRunEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingSecond = new TaskCompletionSource<FlowRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = LayoutRun("first-replay"); var second = LayoutRun("second-loading");
        var client = new FlowClientStub
        {
            RunLoader = id => id == first.Id ? Task.FromResult(first) : pendingSecond.Task,
            RunEventLoader = id => id == first.Id ? pendingReplay.Task : Task.FromResult<IReadOnlyList<FlowRunEvent>>([])
        };
        context.Services.AddSingleton<IFlowApiClient>(client);
        var rendered = context.Render<FlowRunDetails>(p => p.Add(value => value.RunId, first.Id));
        rendered.WaitForAssertion(() => Assert.AreEqual(1, client.EventReadCount));
        Assert.IsTrue(rendered.FindComponent<ResourceDetailsTemplate>().Instance.Busy, "Refresh must stay disabled while the initial replay is pending.");
        rendered.Render(p => p.Add(value => value.RunId, second.Id));
        Assert.IsFalse(rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.HasContent);
        Assert.IsEmpty(rendered.FindAll(".resource-form, .flow-run-overview, .input-request-panel"));
        var beforeReplayCompletion = rendered.RenderCount;
        await rendered.InvokeAsync(() => pendingReplay.SetResult([]));
        rendered.WaitForState(() => rendered.RenderCount > beforeReplayCompletion);
        Assert.IsFalse(rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.HasContent);
        Assert.IsEmpty(rendered.FindAll(".resource-form, .flow-run-overview, .input-request-panel"));
        await rendered.InvokeAsync(() => pendingSecond.SetResult(second));
        rendered.WaitForAssertion(() => Assert.AreEqual(second.Id, rendered.FindComponent<ResourceDetailsTemplate>().Instance.Description));
        Assert.AreEqual(2, client.RunReadCount);
    }

    private static BunitContext RunLayoutContext()
    {
        var context = FlowContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConsoleRealtimeConnectionConfigurator>(new CapturingRealtimeConfigurator());
        context.Services.AddSingleton(TimeProvider.System);
        return context;
    }
    private static FlowRun LayoutRun(string id)
    {
        var workspace = new WorkspaceId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var flow = new FlowId("recorded-definition", new("team"));
        return new FlowRun
        {
            Id = id,
            WorkspaceId = workspace,
            FlowId = flow,
            FlowVersion = "1.0.0",
            DefinitionScopeRef = ResourceScopeRef.Instance,
            Status = FlowRunStatus.Succeeded,
            Scope = FlowRunScope.Workspace(Guid.NewGuid(), workspace, Guid.NewGuid()),
            Input = JsonSerializer.SerializeToElement(new { fixture = true }),
            CreatedAt = FlowClientStub.Now,
            DefinitionSnapshot = new FlowVersion(ResourceScopeRef.Instance, flow, "1.0.0", null, new DirectFlowDefinition(new(FlowTargetKind.Agent, "agent-a")), new Dictionary<string, string>(), FlowClientStub.Now)
        };
    }
}
