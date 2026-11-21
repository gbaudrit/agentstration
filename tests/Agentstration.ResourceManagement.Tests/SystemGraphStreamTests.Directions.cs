using Agentstration.SystemGraph;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Management.Tests;

public sealed partial class SystemGraphStreamTests
{
    [TestMethod]
    public async Task FocusedTraversalUsesSelectedToolSetAccessInsteadOfExpandingEveryMemberOrConsumer()
    {
        var agent = Node("selected-agent"); var other = Node("unselected-agent"); var tool = Node("selected-tool"); var unselected = Node("unselected-tool");
        var set = Node("set"); set = set with { Identity = set.Identity with { Kind = "ToolSet" } };
        var data = Node("data");
        SystemGraphRelation[] edges = [new(agent.Id, set.Id, "declared-reference"), new(other.Id, set.Id, "declared-reference"),
            new(set.Id, tool.Id, "exact-dependency"), new(set.Id, unselected.Id, "exact-dependency"),
            new(agent.Id, tool.Id, "toolset-access") { ViaNodeId = set.Id }, new(tool.Id, data.Id, "exact-dependency")];
        var service = new SystemGraphQueryService([new Source([new([agent, other, tool, unselected, set, data], edges)])], new Access(), TimeProvider.System);
        var dependencies = await Collect(service, new(Root: agent.Id, Direction: SystemGraphDirection.Dependencies));
        Assert.IsFalse(dependencies.SelectMany(frame => frame.Nodes).Any(node => node.Id == unselected.Id));
        var usages = await Collect(service, new(Root: data.Id, Direction: SystemGraphDirection.Usages));
        Assert.IsFalse(usages.SelectMany(frame => frame.Nodes).Any(node => node.Id == other.Id));
        Assert.IsTrue(usages.SelectMany(frame => frame.Relations).Any(edge => edge.Kind == "toolset-access"));
    }

    [TestMethod]
    public async Task FocusedDirectionsTraverseOneWayAcrossNamespacesWithoutChangingArrowDirection()
    {
        var agent = Node("agent"); var tool = Node("tool"); var knowledge = Node("knowledge"); var data = Node("data"); var other = Node("other-agent");
        tool = tool with { Identity = tool.Identity with { Namespace = new("shared") } };
        SystemGraphRelation[] edges = [new(agent.Id, tool.Id, "exact-dependency"), new(tool.Id, knowledge.Id, "dynamic-dependency"),
            new(knowledge.Id, data.Id, "exact-dependency"), new(other.Id, tool.Id, "exact-dependency")];
        var service = new SystemGraphQueryService([new Source([new([agent, tool, knowledge, data, other], edges)])], new Access(), TimeProvider.System);
        var dependencies = await Collect(service, new(Root: agent.Id, Namespace: agent.Identity.Namespace, Direction: SystemGraphDirection.Dependencies));
        CollectionAssert.AreEquivalent(new[] { agent.Id, tool.Id, knowledge.Id, data.Id }, dependencies.SelectMany(frame => frame.Nodes).Select(node => node.Id).ToArray());
        Assert.IsFalse(dependencies.SelectMany(frame => frame.Relations).Any(edge => edge.Source == other.Id));
        var usages = await Collect(service, new(Root: data.Id, MaximumDepth: 3, Direction: SystemGraphDirection.Usages));
        CollectionAssert.AreEquivalent(edges, usages.SelectMany(frame => frame.Relations).ToArray());
        Assert.IsTrue(usages.SelectMany(frame => frame.Relations).All(edge => edges.Contains(edge)), "Incoming traversal preserves consumer-to-dependency arrows.");
        var bounded = await Collect(service, new(Root: data.Id, MaximumDepth: 1, Direction: SystemGraphDirection.Usages));
        CollectionAssert.AreEquivalent(new[] { data.Id, knowledge.Id }, bounded.SelectMany(frame => frame.Nodes).Select(node => node.Id).ToArray());
        Assert.IsTrue(bounded[^1].Truncated);
    }

    [TestMethod]
    public async Task ExactPublicationUsagesDoNotClimbToSiblingPublicationsAndHiddenRootsRemainEmpty()
    {
        var definition = Node("definition"); var a = Node("v1") with { State = "publication" }; var b = Node("v2") with { State = "publication" };
        var first = Node("first-consumer"); var second = Node("second-consumer");
        SystemGraphRelation[] edges = [new(definition.Id, a.Id, "publication"), new(definition.Id, b.Id, "publication"),
            new(first.Id, a.Id, "exact-dependency"), new(second.Id, b.Id, "exact-dependency")];
        var access = new Access();
        var service = new SystemGraphQueryService([new Source([new([definition, a, b, first, second], edges)])], access, TimeProvider.System);
        var frames = await Collect(service, new(Root: a.Id, Direction: SystemGraphDirection.Usages));
        CollectionAssert.AreEquivalent(new[] { a.Id, first.Id }, frames.SelectMany(frame => frame.Nodes).Select(node => node.Id).ToArray());
        access.Hidden.Add(a.Identity.ResourceUid);
        Assert.IsEmpty((await Collect(service, new(Root: a.Id, Direction: SystemGraphDirection.Usages))).SelectMany(frame => frame.Nodes));
        Assert.IsEmpty((await Collect(service, new(Root: "missing", Direction: SystemGraphDirection.Usages))).SelectMany(frame => frame.Nodes));
    }

    [TestMethod]
    public async Task DirectionalCyclesTerminateAndRevocationStillInvalidatesTheObservation()
    {
        var a = Node("a"); var b = Node("b");
        var access = new Access(); var source = new Source([new([a, b], [new(a.Id, b.Id, "exact-dependency"), new(b.Id, a.Id, "exact-dependency")])]);
        var service = new SystemGraphQueryService([source], access, TimeProvider.System);
        var frames = await Collect(service, new(Root: a.Id, Direction: SystemGraphDirection.Usages));
        Assert.AreEqual("complete", frames[^1].Kind); Assert.HasCount(2, frames.SelectMany(frame => frame.Nodes));
        source.OnRead = () => { if (source.Reads % 2 == 0) access.Hidden.Add(b.Identity.ResourceUid); };
        frames = await Collect(service, new(Root: a.Id, Direction: SystemGraphDirection.Usages));
        Assert.AreEqual("error", frames[^1].Kind);
    }

    [TestMethod]
    public async Task InvalidFocusedQueryFailsBeforeEmittingAnyFrame()
    {
        var service = new SystemGraphQueryService([new Source([])], new Access(), TimeProvider.System);
        foreach (var query in new[] { new SystemGraphStreamQuery(Direction: SystemGraphDirection.Usages), new SystemGraphStreamQuery(Root: "root", Direction: (SystemGraphDirection)99) })
            await Assert.ThrowsExactlyAsync<SystemGraphException>(async () => await Collect(service, query));
    }
}
