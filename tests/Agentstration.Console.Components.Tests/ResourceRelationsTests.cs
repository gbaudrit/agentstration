using System.Runtime.CompilerServices;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Components.ResourceRelations;
using Agentstration.Web.Console;
using Agentstration.Web.SystemExplorer;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class ResourceRelationsTests
{
    private static SystemGraphNode Node(string name, ResourceScopeRef? owner = null, string ns = "default")
    {
        var uid = Guid.NewGuid(); return new(SystemGraphIds.Definition(uid), new(uid, owner ?? ResourceScopeRef.Instance, new(ns), "DataSource", name), "definition", new(1));
    }
    private static BunitContext Context(Client client, bool render = true)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISystemGraphStreamClient>(client);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.SystemExplorer/system-explorer.js").Setup<bool>("render", _ => true).SetResult(render);
        return context;
    }
    private static IRenderedComponent<ResourceRelations> Render(BunitContext context, SystemGraphNode root, ResourceScopeRef actor) => context.Render<ResourceRelations>(p => p
        .Add(c => c.Context, actor).Add(c => c.Namespace, root.Identity.Namespace).Add(c => c.Root, root.Id));

    [TestMethod]
    [DataRow("en-US", "Dependencies", "Uses of this resource")]
    [DataRow("fr-FR", "Dépendances", "Utilisations")]
    public async Task SharedControlsLocalizeAndDepthExpansionReusesTheAuthorizedObservation(string culture, string dependencies, string usages)
    {
        using var locale = new TestCultureScope(culture);
        var actor = ResourceScopeRef.Workspace(Guid.NewGuid()); var root = Node("root"); var first = Node("first"); var second = Node("second");
        var client = new Client([root, first, second], [new(root.Id, first.Id, "exact-dependency"), new(first.Id, second.Id, "exact-dependency")]);
        using var context = Context(client); var page = Render(context, root, actor);
        page.WaitForAssertion(() => Assert.AreEqual("false", page.Find("section.resource-relations").GetAttribute("aria-busy")));
        Assert.HasCount(2, page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes);
        Assert.Contains(dependencies, page.Markup); Assert.Contains(usages, page.Markup);
        Assert.AreEqual(actor, client.Calls.Single().Context); Assert.AreEqual(root.Id, client.Calls[0].Query.Root);
        await page.InvokeAsync(() => page.Find("select").ChangeAsync("2"));
        Assert.HasCount(3, page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes); Assert.HasCount(1, client.Calls);
        await page.InvokeAsync(page.Instance.RefreshAsync);
        Assert.HasCount(2, client.Calls); Assert.HasCount(3, page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes);
        Assert.AreEqual("resource", page.FindComponent<SystemGraphDiagram>().Instance.Options!.Mode);
        Assert.AreEqual(root.Id, page.FindComponent<SystemGraphDiagram>().Instance.Options!.Root);
        await page.Find(".relations-view-switch button:nth-child(2)").ClickAsync();
        Assert.HasCount(3, page.FindAll(".relations-list li")); Assert.IsEmpty(page.FindComponents<SystemGraphDiagram>());
    }

    [TestMethod]
    public async Task UsagesAndRecenteringPreserveActorContextAcrossNamespaces()
    {
        var actor = ResourceScopeRef.Workspace(Guid.NewGuid()); var root = Node("root"); var neighbor = Node("neighbor", ns: "shared");
        var client = new Client([root, neighbor], [new(neighbor.Id, root.Id, "exact-dependency")]);
        using var context = Context(client); var page = Render(context, root, actor);
        ResourceRelationsFocus? opened = null;
        page.Render(p => p.Add(c => c.OpenExplorer, focus => opened = focus));
        page.WaitForAssertion(() => Assert.IsFalse(page.Find("section").GetAttribute("aria-busy") == "true"));
        await page.Find(".relations-modes button:nth-child(2)").ClickAsync();
        page.WaitForAssertion(() => Assert.HasCount(2, page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes));
        Assert.AreEqual(SystemGraphDirection.Usages, client.Calls[^1].Query.Direction);
        await page.Find(".relations-view-switch button:nth-child(2)").ClickAsync();
        await page.FindAll(".relations-list button").Single(button => button.GetAttribute("aria-label") == "neighbor").ClickAsync();
        await page.FindAll("aside button").Single(button => button.TextContent.Trim() == "Center on this resource").ClickAsync();
        page.WaitForAssertion(() => Assert.AreEqual(neighbor.Id, client.Calls[^1].Query.Root));
        Assert.IsTrue(client.Calls.All(call => call.Context == actor));
        Assert.AreEqual(neighbor.Identity.Namespace, client.Calls[^1].Query.Namespace);
        await page.Find(".relations-explorer-link").ClickAsync();
        Assert.IsNotNull(opened); Assert.AreEqual(actor, opened.Context); Assert.AreEqual(neighbor.Identity.Namespace, opened.Namespace);
        Assert.AreEqual(neighbor.Id, opened.Root); Assert.AreEqual(SystemGraphDirection.Usages, opened.Direction);
    }

    [TestMethod]
    public async Task ChangedParametersClearOldDataAndIgnoreAnUncooperativeOldResponse()
    {
        var old = Node("old private name"); var next = Node("new visible name");
        var actor = ResourceScopeRef.Workspace(Guid.NewGuid()); var replacement = ResourceScopeRef.Workspace(Guid.NewGuid());
        var client = new Client([next], []) { HoldFirstTail = true, FirstNodes = [old] };
        using var context = Context(client); var page = Render(context, old, actor);
        page.WaitForAssertion(() => Assert.IsTrue(page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes.Any(node => node.Id == old.Id)));
        Assert.AreEqual("true", page.Find("section.resource-relations").GetAttribute("aria-busy"));
        page.Render(p => p.Add(c => c.Context, replacement).Add(c => c.Root, next.Id));
        page.WaitForAssertion(() => Assert.IsTrue(page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes.Any(node => node.Id == next.Id)));
        client.Release.TrySetResult();
        await client.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page.WaitForAssertion(() => Assert.IsTrue(page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes.Any(node => node.Id == next.Id)));
        Assert.IsFalse(page.FindComponent<SystemGraphDiagram>().Instance.Page.Nodes.Any(node => node.Id == old.Id));
        Assert.DoesNotContain("old private name", page.Markup);
        Assert.AreEqual(replacement, client.Calls[^1].Context);
        Assert.IsTrue(client.FirstCancelled);
    }

    [TestMethod]
    public void RendererFailureKeepsTheAccessibleListAndHostileLabelsAreText()
    {
        var root = Node("<img src=x onerror=alert(1)>"); var client = new Client([root], []);
        using var context = Context(client, render: false); var page = Render(context, root, ResourceScopeRef.Instance);
        page.WaitForAssertion(() => Assert.Contains("The graph is unavailable", page.Markup));
        Assert.HasCount(1, page.FindAll(".relations-list li")); Assert.IsEmpty(page.FindAll("img"));
    }

    [TestMethod]
    public void IncompleteBodiesClearProvisionalNodesAndDoNotClaimGlobalAbsence()
    {
        var root = Node("provisional root"); var client = new Client([root], []) { Incomplete = true };
        using var context = Context(client); var page = Render(context, root, ResourceScopeRef.Instance);
        page.WaitForAssertion(() => Assert.Contains("Relationships are unavailable", page.Markup));
        Assert.IsEmpty(page.FindComponents<SystemGraphDiagram>()); Assert.IsEmpty(page.FindAll("aside"));
        Assert.DoesNotContain("provisional root", page.Markup); Assert.Contains("Partial observation", page.Markup);
    }

    private sealed class Client(SystemGraphNode[] nodes, SystemGraphRelation[] edges) : ISystemGraphStreamClient
    {
        public List<(ResourceScopeRef Context, SystemGraphStreamQuery Query)> Calls { get; } = [];
        public bool DelayFirst { get; init; }
        public bool HoldFirstTail { get; init; }
        public SystemGraphNode[]? FirstNodes { get; init; }
        public TaskCompletionSource FirstFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Incomplete { get; init; }
        public bool FirstCancelled { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<SystemGraphStreamFrame> StreamAsync(ResourceScopeRef context, SystemGraphStreamQuery query, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls.Add((context, query)); var first = Calls.Count == 1;
            using var firstRegistration = first ? ct.Register(() => FirstCancelled = true) : default;
            if (DelayFirst && first)
            {
                // Deliberately ignore cancellation until released to exercise stale-response guards.
                using var registration = ct.Register(() => FirstCancelled = true);
                await Release.Task;
            }
            try
            {
                var epoch = Guid.NewGuid();
                yield return new(epoch, 1, "start", DateTimeOffset.UnixEpoch, [], [], []);
                yield return new(epoch, 2, "append", DateTimeOffset.UnixEpoch, first ? FirstNodes ?? nodes : nodes, edges, []);
                if (HoldFirstTail && first) await Release.Task;
                await Task.Yield();
                if (!Incomplete) yield return new(epoch, 3, "complete", DateTimeOffset.UnixEpoch, [], [], []);
            }
            finally { if (first) FirstFinished.TrySetResult(); }
        }
    }
}
