using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Flows;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    public async Task ProgressReadsTheActiveDescendantInTheExactExecutionScopeAndDisplaysItsStep()
    {
        using var context = CreateContext(new KnowledgeClientStub());
        var client = new FlowClientStub();
        var child = PresentationRun("child", "anonymization", [new() { StepName = "agent", StepType = "agent", Status = FlowStepRunStatus.Running, AgentResourceId = "redactor" }]) with { ParentFlowRunId = "root" };
        var root = PresentationRun("root", "orchestration", [new() { StepName = "transform", StepType = "flow", Status = FlowStepRunStatus.Running, ChildFlowRunId = "child", ChildFlowRunIds = ["old", "child"] }]);
        client.Runs[child.Id] = child;
        var active = await FlowRunInspection.ReadActiveAsync(client, root, default);
        Assert.HasCount(1, client.RunReads);
        Assert.AreEqual(root.ExecutionScopeRef, client.RunReads[0].Scope);
        var rendered = context.Render<ExecutionProgress>(parameters => parameters.Add(value => value.Label, "Working")
            .Add(value => value.Runs, active).Add(value => value.CompletedSteps, 2).Add(value => value.TotalSteps, 6));
        Assert.Contains("anonymization · 1.0.0", rendered.Markup);
        Assert.Contains("redactor", rendered.Markup);
        Assert.DoesNotContain("orchestration · 1.0.0", rendered.Markup);
        Assert.Contains("step=agent", rendered.Find("a").GetAttribute("href")!);
        Assert.AreEqual("_blank", rendered.Find("a").GetAttribute("target"));
    }

    [TestMethod]
    public async Task ProgressIgnoresWrongParentOrWrongScopeAndKeepsTheReadableParent()
    {
        var client = new FlowClientStub();
        var root = PresentationRun("root", "orchestration", [new() { StepName = "transform", StepType = "flow", Status = FlowStepRunStatus.Running, ChildFlowRunId = "child" }]);
        client.Runs["child"] = PresentationRun("child", "hidden", []) with { ParentFlowRunId = "unrelated" };
        var active = await FlowRunInspection.ReadActiveAsync(client, root, default);
        Assert.HasCount(1, active);
        client.Runs["child"] = client.Runs["child"] with { ParentFlowRunId = "root", Scope = FlowRunScope.Instance(Guid.Empty) };
        active = await FlowRunInspection.ReadActiveAsync(client, root, default);
        Assert.HasCount(1, active);
        Assert.AreEqual("transform", FlowRunInspection.ActiveLocations(active).Single().Step!.StepName);
    }

    [TestMethod]
    [DataRow("en-US", "A valid JSON result was expected")]
    [DataRow("fr-FR", "Un résultat JSON valide est attendu")]
    public async Task FailedProjectionShowsLocalizedContractExplanationWithoutTheRawEnglishFailure(string locale, string explanation)
    {
        using var culture = new TestCultureScope(locale);
        var source = ProjectedSource();
        var client = new KnowledgeClientStub(source);
        var failed = (await client.StartProjectionAsync(source.Namespace, source.Name, new())).Value with
        { State = KnowledgeProjectionState.Failed, ErrorCode = "data_contract_json_invalid", ErrorMessage = "Raw English runtime failure" };
        client.Projections.Clear();
        client.ProjectionStarter = (_, _) => { client.Projections.Add(failed); return Task.FromResult(new ResourceSnapshot<KnowledgeProjectionResource>(failed, "failed")); };
        await using var context = CreateContext(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/knowledge-sources/" + source.Name + "?view=projections");
        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters.Add(value => value.Name, source.Name));
        await rendered.Find("[data-testid='knowledge-start-projection']").ClickAsync(new MouseEventArgs());
        rendered.WaitForAssertion(() => Assert.Contains(explanation, rendered.Markup));
        Assert.DoesNotContain("Raw English runtime failure", rendered.Markup);
        Assert.HasCount(1, rendered.FindAll(".contract-failure-summary"));
        Assert.IsFalse(rendered.Find("[data-testid='knowledge-start-projection']").HasAttribute("disabled"));
    }

    [TestMethod]
    public void ContractFailureDistinguishesProducerAndContractOwnerAndKeepsTechnicalDataCollapsed()
    {
        using var context = CreateContext(new KnowledgeClientStub());
        var producer = PresentationRun("agent-run", "anonymization", [new() { StepName = "agent", StepType = "agent", AgentResourceId = "redactor" }]);
        var owner = PresentationRun("transform-run", "artifact-processing", [new() { StepName = "transform", StepType = "artifactTransform" }]);
        var failure = new DataContractFailure("agent-output", "flow:transform-run/step:transform/output-media", "$", "json_syntax", "valid JSON", "invalid JSON text", producer.Id, "agent", "data_contract_json_invalid");
        var rendered = context.Render<ContractFailurePanel>(parameters => parameters.Add(value => value.Details, JsonSerializer.Serialize(failure))
            .Add(value => value.Run, producer).Add(value => value.RelatedRuns, [producer, owner]));
        var links = rendered.FindAll("a");
        Assert.HasCount(2, links);
        Assert.Contains("anonymization", links[0].TextContent);
        Assert.Contains("redactor", links[0].TextContent);
        Assert.Contains("artifact-processing", links[1].TextContent);
        Assert.Contains("step=transform", links[1].GetAttribute("href")!);
        Assert.HasCount(1, rendered.FindAll(".contract-failure-summary"));
        Assert.IsFalse(rendered.Find("details").HasAttribute("open"));
        rendered.Render(parameters => parameters.Add(value => value.Compact, true));
        Assert.HasCount(0, rendered.FindAll("details"));
        Assert.HasCount(2, rendered.FindAll("a"));
    }

    private static FlowRun PresentationRun(string id, string flow, IReadOnlyList<FlowStepRun> steps) => new()
    {
        Id = id, FlowId = new(flow), FlowVersion = "1.0.0", Scope = new(Guid.Empty, new WorkspaceId(FlowClientStub.WorkspaceScope.TargetId!.Value), Guid.Empty) { LogicalScopeRef = ResourceScopeRef.Instance },
        Status = FlowRunStatus.Running, Input = JsonSerializer.SerializeToElement(new { }), CreatedAt = DateTimeOffset.UnixEpoch, Steps = steps,
        DefinitionSnapshot = new(FlowClientStub.WorkspaceScope, new(flow), "1.0.0", null, new WorkflowFlowDefinition("input", [], [], []), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch)
    };
}
