using System.Text.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ResourceRelationsPresentationTests
{
    private static SystemGraphNode Node(string kind) { var uid = Guid.NewGuid(); return new(SystemGraphIds.Definition(uid), new(uid, ResourceScopeRef.Instance, ResourceNamespace.Default, kind, kind), "definition"); }
    private static SystemGraphPage Page(SystemGraphNode[] nodes, SystemGraphRelation[] edges) => new(nodes, edges, "partial", DateTimeOffset.UnixEpoch, "partial", false, false, null);

    [TestMethod]
    public void VisibleLevelsContractToolDefinitionsAndNeverPromoteUnselectedToolSetMembers()
    {
        var agent = Node("Agent"); var other = Node("Agent"); var tool = Node("Tool"); var definition = Node("ToolDefinition") with { State = "publication" };
        var knowledge = Node("KnowledgeSource"); var data = Node("DataSource"); var set = Node("ToolSet") with { State = "publication" };
        var input = Page([agent, other, tool, definition, knowledge, data, set],
            [new(agent.Id, tool.Id, "toolset-access") { ViaNodeId = set.Id }, new(other.Id, set.Id, "declared-reference"),
             new(set.Id, tool.Id, "exact-dependency"), new(tool.Id, definition.Id, "exact-dependency"),
             new(definition.Id, knowledge.Id, "dynamic-dependency"), new(knowledge.Id, data.Id, "exact-dependency")]);
        var serialized = JsonSerializer.Serialize(input);
        var first = ResourceRelationsPresentation.Create(input, data.Id, SystemGraphDirection.Usages, 1);
        CollectionAssert.AreEquivalent(new[] { data.Id, knowledge.Id }, first.Summary.Graph.Nodes.Select(node => node.Id).ToArray());
        Assert.IsTrue(first.CanExpand);
        var expanded = ResourceRelationsPresentation.Create(input, data.Id, SystemGraphDirection.Usages, 3);
        CollectionAssert.AreEquivalent(new[] { data.Id, knowledge.Id, tool.Id, agent.Id }, expanded.Summary.Graph.Nodes.Select(node => node.Id).ToArray());
        Assert.AreEqual(set.Id, expanded.Summary.Links.Single(link => link.Source.Id == agent.Id).Paths.Single().Via!.Id);
        Assert.AreEqual(definition.Id, expanded.Summary.Links.Single(link => link.Target.Id == tool.Id).ToolDefinitions.Single().Id);
        Assert.AreEqual(serialized, JsonSerializer.Serialize(input));
    }

    [TestMethod]
    public void CyclesAndLargeNeighborhoodsStayBoundedWithTheExactRootPinned()
    {
        var root = Node("DataSource"); var siblings = Enumerable.Range(0, 120).Select(_ => Node("Tool")).ToArray();
        var input = Page([root, .. siblings], [.. siblings.Select(node => new SystemGraphRelation(node.Id, root.Id, "exact-dependency")), new(root.Id, siblings[0].Id, "dynamic-dependency")]);
        var result = ResourceRelationsPresentation.Create(input, root.Id, SystemGraphDirection.Usages, 3);
        Assert.HasCount(ResourceRelationsPresentation.NodeLimit, result.Summary.Graph.Nodes); Assert.AreEqual(root.Id, result.Summary.Graph.Nodes[0].Id);
        Assert.IsTrue(result.Limited); Assert.IsFalse(result.Summary.Graph.GloballyComplete);
        var ids = result.Summary.Graph.Nodes.Select(node => node.Id).ToHashSet();
        Assert.IsTrue(result.Summary.Graph.Relations.All(edge => ids.Contains(edge.Source) && ids.Contains(edge.Target)));
        Assert.IsEmpty(ResourceRelationsPresentation.Create(input, "missing", SystemGraphDirection.Usages, 1).Summary.Graph.Nodes);
    }
}
