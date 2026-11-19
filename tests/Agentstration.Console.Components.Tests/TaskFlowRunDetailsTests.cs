using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class TaskFlowRunDetailsTests
{
    [TestMethod]
    public void SavedRunUsesRealIdentityAndRetainsExecutionAcrossFragmentsWithoutReloading()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new RunHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var context = Context(http);
        var task = Guid.NewGuid();
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/tasks/{task:D}/flowruns/{handler.Run.Id}");
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult(nav.Uri + "#overview");
        var rendered = context.Render<TaskFlowRunDetails>(p => p.Add(c => c.TaskId, task).Add(c => c.RunId, handler.Run.Id));
        rendered.WaitForElement("[data-task-run-section=overview]");
        StringAssert.Contains(rendered.Find("[data-task-run-section=overview]").TextContent, handler.Run.Id);
        StringAssert.Contains(rendered.Find("[data-task-run-section=overview]").TextContent, task.ToString());
        StringAssert.Contains(rendered.Find("[data-task-run-section=overview]").TextContent, "task-review/task-flow");
        StringAssert.Contains(rendered.Find("[data-task-run-section=overview]").TextContent, handler.Run.ExecutionScopeRef.Value);
        Assert.AreEqual($"/api/tasks/{task:D}/flow-runs/{handler.Run.Id}", handler.Requests.Single());
        Assert.AreEqual(0, rendered.FindAll("[data-task-run-section=execution]").Count);
        nav.NavigateTo(nav.Uri.Split('#')[0] + "#execution");
        rendered.WaitForElement("[data-task-run-section=execution]");
        StringAssert.Contains(rendered.Find(".io-panel").TextContent, "kept-input");
        var topology = rendered.FindComponent<FlowTopologyViewer>().Instance;
        nav.NavigateTo(nav.Uri.Split('#')[0] + "#overview");
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Find("[data-task-run-section=execution]").HasAttribute("hidden")));
        nav.NavigateTo(nav.Uri.Split('#')[0] + "#execution");
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-task-run-section=execution]").HasAttribute("hidden")));
        Assert.AreSame(topology, rendered.FindComponent<FlowTopologyViewer>().Instance);
        Assert.HasCount(1, handler.Requests);
        Assert.AreEqual(0, rendered.FindAll(".resource-header [role=switch]").Count);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public void FailedReadsUseCommonNotFoundAndRetryStates(HttpStatusCode status)
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new RunHandler { Status = status };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var context = Context(http);
        var rendered = context.Render<TaskFlowRunDetails>(p => p.Add(c => c.TaskId, Guid.NewGuid()).Add(c => c.RunId, handler.Run.Id));
        rendered.WaitForAssertion(() => Assert.AreEqual(status == HttpStatusCode.NotFound ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Failed,
            rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status));
        Assert.IsTrue(rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.BlocksContent);
        Assert.AreEqual(status == HttpStatusCode.ServiceUnavailable, rendered.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.CanRetry);
        Assert.IsEmpty(rendered.FindAll("[data-task-run-section]"));
    }

    private static BunitContext Context(HttpClient http)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IWorkApiClient>(new WorkApiClient(http));
        return context;
    }

    private sealed class RunHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Requests { get; } = [];
        public FlowRun Run { get; set; } = CreateRun();
        public TaskCompletionSource<HttpResponseMessage>? NextRead { get; set; }
        public bool ReadPending { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            if (NextRead is { } pending) { NextRead = null; ReadPending = true; return pending.Task; }
            var task = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];
            var value = Run with { WorkTaskId = Run.WorkTaskId ?? task };
            return Task.FromResult(new HttpResponseMessage(Status) { Content = Status == HttpStatusCode.OK ? JsonContent.Create(value) : JsonContent.Create(new { title = "Unavailable" }) });
        }
        private static FlowRun CreateRun()
        {
            var workspace = WorkspaceId.New();
            var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"));
            var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
            return new()
            {
                Id = "task-flow-run",
                FlowId = new("task-flow", ResourceNamespace.Parse("task-review")),
                FlowVersion = "1.0.0",
                Scope = new(Guid.NewGuid(), workspace, Guid.NewGuid()),
                Input = JsonSerializer.SerializeToElement(new { request = "kept-input" }),
                CreatedAt = now,
                Status = FlowRunStatus.Succeeded,
                DefinitionSnapshot = new(ResourceScopeRef.Instance, new("task-flow", ResourceNamespace.Parse("task-review")), "1.0.0", null, definition, new Dictionary<string, string>(), now)
            };
        }
    }
}
