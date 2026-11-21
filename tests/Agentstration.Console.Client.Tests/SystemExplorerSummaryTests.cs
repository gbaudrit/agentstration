using System.Text.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SystemExplorerSummaryTests
{
    private static SystemGraphNode Node(string kind, string name) { var uid = Guid.NewGuid(); return new(SystemGraphIds.Definition(uid), new(uid, ResourceScopeRef.Instance, ResourceNamespace.Default, kind, name), "definition", new(1)); }
    [TestMethod]
    public void SummaryKeepsSelectedGrantsAndExactProvenanceWithoutPromotingUnselectedMembers()
    {
        var agent = Node("Agent", "Agent"); var tool = Node("Tool", "Read"); var other = Node("Tool", "Read");
        var set = Node("ToolSet", "Nantes access") with { State = "publication", Evidence = new(Publication: "2.0.0") };
        var definition = Node("ToolDefinition", "Relay") with { State = "publication", Evidence = new(Publication: "1.0.0") };
        var knowledge = Node("KnowledgeSource", "Nantes");
        var pin = tool with { Id = SystemGraphIds.Generation(tool.Identity.ResourceUid, 1), State = "recorded-generation" };
        var current = tool with { Evidence = new(3) };
        SystemGraphRelation[] edges = [new(agent.Id, set.Id, "declared-reference"), new(set.Id, pin.Id, "exact-dependency"),
            new(set.Id, other.Id, "exact-dependency"), new(agent.Id, pin.Id, "toolset-access") { ViaNodeId = set.Id },
            new(pin.Id, current.Id, "generation-of"), new(current.Id, definition.Id, "exact-dependency", "materialization.publication"),
            new(definition.Id, knowledge.Id, "dynamic-dependency")];
        var observation = new SystemGraphPage([agent, pin, current, other, set, definition, knowledge], edges, "snapshot", DateTimeOffset.UnixEpoch, "partial", false, false, null);
        var before = JsonSerializer.Serialize(observation);
        var summary = SystemExplorerSummaries.Create(observation, observation, agent.Id);
        Assert.HasCount(3, summary.Graph.Nodes); Assert.IsFalse(summary.Graph.Nodes.Any(node => node.Identity.Kind is "ToolSet" or "ToolDefinition"));
        Assert.HasCount(1, summary.Graph.Nodes.Where(node => node.Identity.Kind == "Tool"));
        var access = summary.Links.Single(link => link.Source.Id == agent.Id);
        Assert.AreEqual(set, access.Paths.Single().Via); Assert.AreEqual(definition, access.ToolDefinitions.Single());
        Assert.AreEqual("materialization.publication", access.Provenance.Single().SourceField);
        Assert.IsTrue(summary.Links.Any(link => link.Target.Id == knowledge.Id && link.Paths.Single().Steps.Count == 2));
        Assert.AreEqual(before, JsonSerializer.Serialize(observation));
    }
    [TestMethod]
    public void IndependentToolSetsAggregateOnOneToolWhileHomonymsAndPublicationsRemainDistinct()
    {
        var agent = Node("Agent", "A"); var tool = Node("Tool", "same"); var homonym = Node("Tool", "same");
        var set1 = Node("ToolSet", "set") with { State = "publication", Evidence = new(Publication: "1") };
        var set2 = set1 with { Id = SystemGraphIds.Publication(set1.Identity.ResourceUid, "2"), Evidence = new(Publication: "2") };
        var page = new SystemGraphPage([agent, tool, homonym, set1, set2],
            [new(agent.Id, tool.Id, "toolset-access") { ViaNodeId = set1.Id }, new(agent.Id, tool.Id, "toolset-access") { ViaNodeId = set2.Id },
             new(agent.Id, homonym.Id, "declared-reference", "definition.tools[0]")], "s", DateTimeOffset.UnixEpoch, "partial", false, false, null);
        var summary = SystemExplorerSummaries.Create(page, page, agent.Id);
        Assert.HasCount(2, summary.Graph.Nodes.Where(node => node.Identity.Kind == "Tool"));
        Assert.HasCount(2, summary.Links.Single(link => link.Target.Id == tool.Id).Paths);
        Assert.AreEqual("direct", summary.Links.Single(link => link.Target.Id == homonym.Id).Paths.Single().Kind);
        var missing = page with { Nodes = page.Nodes.Where(node => node.Id != set2.Id).ToArray() };
        Assert.HasCount(1, SystemExplorerSummaries.Create(missing, missing, agent.Id).Links.Single(link => link.Target.Id == tool.Id).Paths);
    }
}
