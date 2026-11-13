using Agentstration.Flows;
using System.Reflection;
using System.Text.Json;
using Agentstration.Web.Console;
using Agentstration.Web.Components.Pages;
using Agentstration.Resources;
using Agentstration.Web.Components.Flows;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowProcessSummaryTests
{
    [TestMethod]
    public void HistorySeparatesProcessUnitsAndHonorsTheExactExecutionScope()
    {
        using var locale = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = DispatchProxy.Create<IFlowApiClient, HistoryClient>();
        var proxy = (HistoryClient)(object)client;
        var workspace = WorkspaceId.New();
        var scope = ResourceScopeRef.Workspace(workspace.Value);
        var version = new FlowVersion(ResourceScopeRef.Instance, new("source"), "1.0.0", null,
            new DirectFlowDefinition(new(FlowTargetKind.Agent, "unused")), new Dictionary<string, string>(), DateTimeOffset.UtcNow);
        var root = new FlowRun { Id = "root", WorkspaceId = workspace, FlowId = new("source"), FlowVersion = "1.0.0",
            Scope = new(Guid.NewGuid(), workspace, Guid.NewGuid()), Input = JsonSerializer.SerializeToElement(new { }), CreatedAt = DateTimeOffset.UtcNow,
            DefinitionSnapshot = version };
        var definition = new FlowProcessDefinitionOrigin(root.FlowId, ResourceScopeRef.Instance, "1.0.0", "snapshot", "source-hash", "transform", "Fragments", "process", "body-hash");
        proxy.Runs = [root, root with { Id = "unit", ParentFlowRunId = root.Id, ProcessOrigin = new(definition, root.Id, "transform", 1, Guid.NewGuid(), "unit-0", 0, null) }];
        context.Services.AddSingleton(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/flow-runs?scopeRef=" + Uri.EscapeDataString(scope.Value));
        var rendered = context.Render<FlowRuns>();
        rendered.WaitForAssertion(() => Assert.AreEqual(scope, proxy.Scope));
        Assert.DoesNotContain("Process of Fragments", rendered.Markup);
        rendered.Find("select[aria-label='Execution kind']").Change("process");
        rendered.WaitForAssertion(() => Assert.Contains("Process of Fragments", rendered.Markup));
        Assert.Contains("scopeRef=" + Uri.EscapeDataString(scope.Value), rendered.Find("a.resource-name-link").GetAttribute("href")!);
    }

    public class HistoryClient : DispatchProxy
    {
        public IReadOnlyList<FlowRun> Runs { get; set; } = [];
        public ResourceScopeRef? Scope { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "GetFlowRunsAsync") throw new NotSupportedException();
            Scope = args?.OfType<ResourceScopeRef>().FirstOrDefault();
            return Task.FromResult(Runs);
        }
    }

    [TestMethod]
    [DataRow("en-US", "Process units", "Discovering work units", "Unit 1")]
    [DataRow("fr-FR", "Unités du process", "Découverte des unités en cours", "Unité 1")]
    public void ProcessProgressShowsKnownAndUnknownTotalsAndExactScopedUnitLinks(string culture, string heading, string discovering, string unitLabel)
    {
        using var locale = new TestCultureScope(culture);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var admission = new FlowProcessAdmission(2, "process", false, [new("fragment-A", 0, "child-unit", null)]);
        var rendered = context.Render<FlowProcessSummary>(parameters => parameters.Add(component => component.Admission, admission)
            .Add(component => component.OwnerRunId, "parent-run").Add(component => component.StepName, "transform")
            .Add(component => component.Scope, scope).Add(component => component.LogicalScope, ResourceScopeRef.Instance));
        Assert.Contains(heading, rendered.Markup);
        Assert.Contains(discovering, rendered.Markup);
        Assert.IsNull(rendered.Find("progress").GetAttribute("value"));
        var unit = rendered.FindAll("li a").Single();
        Assert.AreEqual(unitLabel, unit.TextContent);
        Assert.Contains("scopeRef=" + Uri.EscapeDataString(scope.Value), unit.GetAttribute("href")!);
        Assert.Contains("context=%2Finstance", unit.GetAttribute("href")!);
        Assert.Contains("parent-run", rendered.Find("summary a").GetAttribute("href")!);
        rendered.Render(parameters => parameters.Add(component => component.Admission, admission with { Closed = true, TotalUnits = 1 }));
        Assert.AreEqual("1", rendered.Find("progress").GetAttribute("max"));
        Assert.AreEqual("0", rendered.Find("progress").GetAttribute("value"));
    }
}
