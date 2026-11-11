using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Web.Components.Flows;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowRunOutputTests
{
    [TestMethod]
    [DataRow("en-US", "temporary content")]
    [DataRow("fr-FR", "contenu temporaire")]
    public void ExpiredDirectOutputStillLinksTheProducedArtifact(string language, string explanation)
    {
        using var culture = new TestCultureScope(language);
        using var context = new BunitContext();
        var client = new ArtifactAdministrationComponentTests.ArtifactClient("output");
        var run = Run(client.Durable.WorkspaceId) with { TransientPayloads = true, TransientPayloadsAvailable = false };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IArtifactsClient>(client);
        var rendered = context.Render<FlowRunOutput>(p => p.Add(c => c.Run, run));
        Assert.Contains(explanation, rendered.Find("[data-testid='flow-run-output-expired']").TextContent);
        var link = rendered.Find("[data-testid='flow-run-output-artifact'] a");
        Assert.AreEqual($"/artifacts/durable/{client.Durable.ArtifactId}?context=%2Finstance", link.GetAttribute("href"));
        Assert.AreEqual(0, client.ContentReadCount);
    }

    [TestMethod]
    public void OutputCatalogueFiltersUnrelatedAndForeignWorkspaceArtifactsAndCanRefreshAfterPublication()
    {
        using var context = new BunitContext();
        var client = new ArtifactAdministrationComponentTests.ArtifactClient("output") { DurableCatalog = [] };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IArtifactsClient>(client);
        var run = Run(client.Durable.WorkspaceId);
        var rendered = context.Render<FlowRunOutput>(p => p.Add(c => c.Run, run));
        Assert.IsNotNull(rendered.Find("[data-testid='flow-run-output-empty']"));
        Assert.AreEqual(0, rendered.FindAll("[data-testid='flow-run-output-artifact']").Count);
        client.DurableCatalog = [client.Durable, client.Durable with { WorkspaceId = WorkspaceId.New() },
            client.Durable with { ProducerFlowRunId = "another-run" }];
        rendered.Find("[data-testid='flow-run-output-refresh']").Click();
        Assert.AreEqual(1, rendered.FindAll("[data-testid='flow-run-output-artifact']").Count);
        Assert.AreEqual(0, client.ContentReadCount);
        rendered.Render(p => p.Add(c => c.Run, run with { WorkspaceId = WorkspaceId.New() }));
        Assert.AreEqual(0, rendered.FindAll("[data-testid='flow-run-output-artifact']").Count);
        // The private processing child must find the same output via its recorded provenance.
        client.DurableCatalog = [client.Durable with { ProducerFlowRunId = "parent-run", Receipt = client.Durable.Receipt with
            { Provenance = new Dictionary<string, string> { ["processingRunId"] = "child-run" } } }];
        rendered.Render(p => p.Add(c => c.Run, run with { Id = "child-run" }));
        Assert.AreEqual(1, rendered.FindAll("[data-testid='flow-run-output-artifact']").Count);
    }

    [TestMethod]
    public void CatalogueFailureDoesNotHideTheReturnedFlowOutput()
    {
        using var context = new BunitContext();
        var client = new ArtifactAdministrationComponentTests.ArtifactClient("output") { RejectDurableCatalog = true };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IArtifactsClient>(client);
        var run = Run(client.Durable.WorkspaceId) with { Output = JsonSerializer.SerializeToElement(new { artifacts = new[] { "output-reference" } }) };
        var rendered = context.Render<FlowRunOutput>(p => p.Add(c => c.Run, run));
        Assert.Contains("output-reference", rendered.Find("pre").TextContent);
        Assert.Contains("could not be loaded", rendered.Markup);
        client.RejectDurableCatalog = false;
        rendered.Find("[data-testid='flow-run-output-refresh']").Click();
        Assert.AreEqual(1, rendered.FindAll("[data-testid='flow-run-output-artifact']").Count);
    }

    [TestMethod]
    public void PublishedExistingArtifactAppearsAsAnOutputWithoutChangingItsProducer()
    {
        using var context = new BunitContext();
        var client = new ArtifactAdministrationComponentTests.ArtifactClient("output");
        client.Durable = client.Durable with { ProducerFlowRunId = "original-acquisition" };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IArtifactsClient>(client);
        var run = Run(client.Durable.WorkspaceId) with { Output = JsonSerializer.SerializeToElement(new
            { artifacts = new[] { new { artifactId = client.Durable.ArtifactId.ToString(), kind = "durable" } } }) };
        var rendered = context.Render<FlowRunOutput>(p => p.Add(c => c.Run, run));
        Assert.AreEqual(1, rendered.FindAll("[data-testid='flow-run-output-artifact']").Count);
        Assert.AreEqual("original-acquisition", client.Durable.ProducerFlowRunId);
        Assert.AreEqual(0, client.ContentReadCount);
    }

    private static FlowRun Run(WorkspaceId workspace) => new()
    {
        Id = "flow-run-1", WorkspaceId = workspace, FlowId = new("knowledge-orchestration-builtin"), FlowVersion = "1.0.0",
        Scope = new(Guid.NewGuid(), workspace, Guid.NewGuid()) { LogicalScopeRef = ResourceScopeRef.Instance },
        DefinitionSnapshot = new FlowVersion(ResourceScopeRef.Instance, new("knowledge-orchestration-builtin"), "1.0.0", null,
            new DirectFlowDefinition(new(FlowTargetKind.Agent, "unused")), new Dictionary<string, string>(), DateTimeOffset.UtcNow),
        Input = JsonSerializer.SerializeToElement(new { }), Status = FlowRunStatus.Succeeded, CreatedAt = DateTimeOffset.UtcNow
    };
}
