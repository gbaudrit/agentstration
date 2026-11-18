using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;
using Agentstration.Systems.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class SystemExplorerTests
{
    [TestMethod]
    [DataRow("en-US", "Explore dependencies")]
    [DataRow("fr-FR", "Explorer les dépendances")]
    public async Task PairedProvidersAndTheirRecordedPinsUseTheAuthorizedNameWithoutConfusingHomonyms(string culture, string explore)
    {
        using var locale = new TestCultureScope(culture);
        var client = new StreamClient(); using var context = StreamContext(client);
        var page = context.Render<Agentstration.Web.Components.Pages.SystemExplorer>(); var epoch = Guid.NewGuid();
        var provider = StreamNode(client.Owner, "paired-technical-id");
        provider = provider with { Identity = provider.Identity with { Kind = "ToolProvider" }, DisplayName = "Friendly extension" };
        var pin = provider with { Id = SystemGraphIds.Generation(provider.Identity.ResourceUid, 3), State = "recorded-generation", Evidence = new(3), DisplayName = null };
        var homonym = StreamNode(client.Owner, "paired-technical-id");
        homonym = homonym with { Identity = homonym.Identity with { Kind = "ToolProvider", Namespace = new("other") }, DisplayName = "Other extension" };
        await client.Push(client.Owner, new(epoch, 1, "start", DateTimeOffset.UnixEpoch, [], [], []));
        await client.Push(client.Owner, new(epoch, 2, "append", DateTimeOffset.UnixEpoch, [pin, homonym], [], []));
        page.WaitForAssertion(() => Assert.HasCount(2, page.FindAll("[data-testid=explorer-node]")));
        await client.Push(client.Owner, new(epoch, 3, "append", DateTimeOffset.UnixEpoch, [provider], [], []));
        page.WaitForAssertion(() => Assert.AreEqual("Friendly extension", page.FindAll("[data-testid=explorer-node]")[0].QuerySelector("button")!.TextContent));
        Assert.AreEqual("Other extension", page.FindAll("[data-testid=explorer-node]")[1].QuerySelector("button")!.TextContent);
        Assert.AreEqual(explore, page.FindAll("[data-testid=explorer-node]")[0].QuerySelector("a")!.TextContent);
        Assert.Contains("namespace=other", page.FindAll("[data-testid=explorer-node]")[1].QuerySelector("a")!.GetAttribute("href")!);
        page.WaitForAssertion(() =>
        {
            var diagram = (SystemGraphPage)context.JSInterop.Invocations.Last(call => call.Identifier == "render").Arguments[1]!;
            Assert.StartsWith("Friendly extension", diagram.Nodes.Single(node => node.Id == pin.Id).DisplayName!);
            Assert.AreEqual("Other extension", diagram.Nodes.Single(node => node.Id == homonym.Id).DisplayName);
            Assert.AreEqual(3, diagram.Nodes.Single(node => node.Id == pin.Id).Evidence!.Generation);
        });
        await page.FindAll("[data-testid=explorer-node]")[0].QuerySelector("button")!.ClickAsync();
        page.WaitForAssertion(() => Assert.AreEqual("Friendly extension", page.Find("[data-testid=explorer-selection] h2").TextContent));
        Assert.Contains(pin.Id, page.Find("[data-testid=explorer-selection]").TextContent);
        await client.Push(client.Owner, new(epoch, 4, "complete", DateTimeOffset.UnixEpoch, [], [], []));
        client.For(client.Owner).Writer.TryComplete();
    }

    [TestMethod]
    [DataRow("en-US", "authorized resources received", "Next page")]
    [DataRow("fr-FR", "ressources autorisées reçues", "Page suivante")]
    public async Task BatchesRenderBeforeTailAndSystemsWithoutRelayoutOutsideTheBoundedWindow(string culture, string progress, string next)
    {
        using var locale = new TestCultureScope(culture);
        var client = new StreamClient { HoldSystems = true };
        using var context = StreamContext(client);
        var page = context.Render<Agentstration.Web.Components.Pages.SystemExplorer>();
        var epoch = Guid.NewGuid(); long sequence = 0;
        Task Push(string kind, params SystemGraphNode[] nodes) => client.Push(client.Owner, new(epoch, ++sequence, kind,
            DateTimeOffset.UnixEpoch, nodes, [], nodes.Length == 0 ? [] : [new(client.Owner, null)]));
        await Push("start");
        await Push("append", StreamNode(client.Owner, "first"));
        page.WaitForElement("[data-testid=explorer-node]");
        Assert.Contains(progress, page.Find(".explorer-loading").TextContent);
        Assert.IsFalse(client.Systems.Task.IsCompleted, "Systems loading must not block the graph.");
        for (var batch = 0; batch < 4; batch++)
        {
            var count = batch == 3 ? 19 : 20;
            await Push("append", Enumerable.Range(0, count).Select(index => StreamNode(client.Owner, $"node-{batch}-{index}")).ToArray());
        }
        page.WaitForAssertion(() => Assert.HasCount(80, page.FindAll("[data-testid=explorer-node]")));
        page.WaitForAssertion(() => Assert.AreEqual(80, ((SystemGraphPage)context.JSInterop.Invocations.Last(call => call.Identifier == "render").Arguments[1]!).Nodes.Count));
        var renders = context.JSInterop.Invocations.Count(call => call.Identifier == "render");
        await Push("append", Enumerable.Range(0, 20).Select(index => StreamNode(client.Owner, $"tail-{index}")).ToArray());
        page.WaitForAssertion(() => Assert.Contains("100", page.Find(".explorer-loading").TextContent));
        Assert.AreEqual(renders, context.JSInterop.Invocations.Count(call => call.Identifier == "render"));
        await page.FindAll(".explorer-actions button").Single(button => button.TextContent == next).ClickAsync();
        page.WaitForAssertion(() => Assert.HasCount(20, page.FindAll("[data-testid=explorer-node]")));
        Assert.Contains("tail-0", page.Markup); Assert.DoesNotContain(">first<", page.Markup, StringComparison.Ordinal);
        await Push("complete"); client.For(client.Owner).Writer.TryComplete(); client.Systems.TrySetResult([]);
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindAll(".explorer-loading")));
    }

    [TestMethod]
    [DataRow("error")]
    [DataRow("eof")]
    [DataRow("gap")]
    public async Task FailedOrIncompleteStreamsRemoveAllPreviouslyDisplayedIdentities(string failure)
    {
        var client = new StreamClient(); using var context = StreamContext(client);
        var page = context.Render<Agentstration.Web.Components.Pages.SystemExplorer>(); var epoch = Guid.NewGuid();
        await client.Push(client.Owner, new(epoch, 1, "start", DateTimeOffset.UnixEpoch, [], [], []));
        await client.Push(client.Owner, new(epoch, 2, "append", DateTimeOffset.UnixEpoch, [StreamNode(client.Owner, "previously-authorized")], [], []));
        page.WaitForElement("[data-testid=explorer-node]");
        if (failure == "error") await client.Push(client.Owner, new(epoch, 3, "error", DateTimeOffset.UnixEpoch, [], [], [], ErrorCode: "private-diagnostic"));
        if (failure == "gap") await client.Push(client.Owner, new(epoch, 4, "append", DateTimeOffset.UnixEpoch, [], [], []));
        client.For(client.Owner).Writer.TryComplete();
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindAll("[data-testid=explorer-node]")));
        Assert.DoesNotContain("previously-authorized", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("private-diagnostic", page.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ChangingContextCancelsThePreviousBodyAndIgnoresItsLateFrames()
    {
        var client = new StreamClient(); using var context = StreamContext(client);
        var page = context.Render<Agentstration.Web.Components.Pages.SystemExplorer>(); var oldEpoch = Guid.NewGuid();
        await client.Push(client.Owner, new(oldEpoch, 1, "start", DateTimeOffset.UnixEpoch, [], [], []));
        await client.Push(client.Owner, new(oldEpoch, 2, "append", DateTimeOffset.UnixEpoch, [StreamNode(client.Owner, "old-context")], [], []));
        page.WaitForElement("[data-testid=explorer-node]");
        var next = ResourceScopeRef.Workspace(Guid.NewGuid()); var epoch = Guid.NewGuid();
        await page.InvokeAsync(() => context.Services.GetRequiredService<NavigationManager>().NavigateTo("/system-explorer?context=" + Uri.EscapeDataString(next.Value)));
        await client.Push(next, new(epoch, 1, "start", DateTimeOffset.UnixEpoch, [], [], []));
        await client.Push(next, new(epoch, 2, "append", DateTimeOffset.UnixEpoch, [StreamNode(next, "new-context")], [], []));
        await client.Push(client.Owner, new(oldEpoch, 3, "error", DateTimeOffset.UnixEpoch, [], [], []));
        page.WaitForAssertion(() => Assert.Contains("new-context", page.Markup));
        Assert.DoesNotContain("old-context", page.Markup, StringComparison.Ordinal);
        Assert.IsTrue(client.Cancelled.Contains(client.Owner));
        await client.Push(next, new(epoch, 3, "complete", DateTimeOffset.UnixEpoch, [], [], []));
        client.For(next).Writer.TryComplete();
    }

    [TestMethod]
    public async Task NeighbourWindowsKeepTheRootAndNeverSendMoreThanEightyNodesToTheRenderer()
    {
        var client = new StreamClient(); using var context = StreamContext(client);
        var root = StreamNode(client.Owner, "root"); var epoch = Guid.NewGuid();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/system-explorer?root=" + Uri.EscapeDataString(root.Id));
        var page = context.Render<Agentstration.Web.Components.Pages.SystemExplorer>();
        await client.Push(client.Owner, new(epoch, 1, "start", DateTimeOffset.UnixEpoch, [], [], []));
        await client.Push(client.Owner, new(epoch, 2, "append", DateTimeOffset.UnixEpoch,
            [root, .. Enumerable.Range(0, 119).Select(index => StreamNode(client.Owner, "neighbour-" + index))], [], []));
        page.WaitForAssertion(() => Assert.HasCount(80, page.FindAll("[data-testid=explorer-node]")));
        await page.FindAll(".explorer-actions button").Single(button => button.TextContent == "Next page").ClickAsync();
        page.WaitForAssertion(() => Assert.HasCount(41, page.FindAll("[data-testid=explorer-node]")));
        Assert.Contains("root", page.FindAll("[data-testid=explorer-node]")[0].TextContent);
        Assert.AreEqual(root.Id, page.FindComponent<SystemOperationsPanel>().Instance.Root);
        foreach (var call in context.JSInterop.Invocations.Where(call => call.Identifier == "render"))
            Assert.IsLessThanOrEqualTo(80, ((SystemGraphPage)call.Arguments[1]!).Nodes.Count);
        await client.Push(client.Owner, new(epoch, 3, "complete", DateTimeOffset.UnixEpoch, [], [], []));
        client.For(client.Owner).Writer.TryComplete();
    }

    private static BunitContext StreamContext(StreamClient client)
    {
        var context = new BunitContext();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISystemExplorerClient>(client);
        context.Services.AddSingleton<ISystemOperationsClient>(new OperationsClient());
        context.Services.AddSingleton<IConsoleContextProvider>(new Provider(client.Owner));
        context.Services.AddSingleton<IConsoleNavigationContext>(new Navigation(client.Owner));
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.SystemExplorer/system-explorer.js").Setup<bool>("render", _ => true).SetResult(true);
        return context;
    }
    private static SystemGraphNode StreamNode(ResourceScopeRef owner, string name)
    {
        var uid = Guid.NewGuid(); return new(SystemGraphIds.Definition(uid), new(uid, owner, ResourceNamespace.Default, "Agent", name), "definition");
    }
    private sealed class StreamClient : ISystemExplorerClient
    {
        public ResourceScopeRef Owner { get; init; } = ResourceScopeRef.Workspace(Guid.NewGuid());
        private readonly ConcurrentDictionary<ResourceScopeRef, Channel<SystemGraphStreamFrame>> streams = new();
        public ConcurrentQueue<ResourceScopeRef> Cancelled { get; } = new();
        public TaskCompletionSource<IReadOnlyList<SystemView>> Systems { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldSystems { get; init; }
        public AgentDefinitionCatalog? Catalog { get; set; }
        public TaskCompletionSource<AgentDefinitionCatalog?>? HeldCatalog { get; set; }
        public IReadOnlyList<AgentDeploymentView> Deployments { get; set; } = [];
        public SystemGraphPage? Photo { get; set; }
        public List<string?> Roots { get; } = [];
        public List<ResourceNamespace> RootNamespaces { get; } = [];
        public AgentDeploymentAssignmentRequest? SavedAssignment { get; private set; }
        public string? SavedETag { get; private set; }
        public Task<AgentDeploymentView> AssignAsync(ResourceScopeRef context, AgentDeploymentAssignmentRequest value, string? etag, bool create, CancellationToken ct)
        {
            SavedAssignment = value; SavedETag = etag;
            return Task.FromResult(Deployments.Single(deployment => deployment.Name == value.Name));
        }
        public SystemOperationalSnapshot Observation { get; set; } = new(null, DateTimeOffset.UnixEpoch, "unavailable");
        public Task<AgentDefinitionCatalog?> DefinitionsAsync(ResourceScopeRef context, Guid uid, CancellationToken ct) => HeldCatalog?.Task ?? Task.FromResult(Catalog);
        public Task<IReadOnlyList<AgentDeploymentView>> DeploymentsAsync(ResourceScopeRef context, ResourceNamespace ns, Guid? agentUid, CancellationToken ct) => Task.FromResult(Deployments);
        public Task<SystemOperationalSnapshot> ObservedDeploymentAsync(ResourceScopeRef context, ResourceNamespace ns, string root, CancellationToken ct) => Task.FromResult(Observation);
        public Channel<SystemGraphStreamFrame> For(ResourceScopeRef scope) => streams.GetOrAdd(scope, _ => Channel.CreateUnbounded<SystemGraphStreamFrame>());
        public Task Push(ResourceScopeRef scope, SystemGraphStreamFrame frame) => For(scope).Writer.WriteAsync(frame).AsTask();
        public async IAsyncEnumerable<SystemGraphStreamFrame> StreamAsync(ResourceScopeRef context, ResourceNamespace ns, string? root,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Roots.Add(root); RootNamespaces.Add(ns);
            if (Photo is not null)
            {
                var epoch = Guid.NewGuid();
                yield return new(epoch, 1, "start", Photo.ObservedAt, [], [], []);
                yield return new(epoch, 2, "append", Photo.ObservedAt, Photo.Nodes, Photo.Relations, Photo.Scopes);
                yield return new(epoch, 3, "complete", Photo.ObservedAt, [], [], []);
                yield break;
            }
            using var cancelled = ct.Register(() => Cancelled.Enqueue(context));
            await foreach (var frame in For(context).Reader.ReadAllAsync(ct)) yield return frame;
        }
        public Task<SystemGraphPage> ReadAsync(ResourceScopeRef context, ResourceNamespace ns, string? root, string? cursor, CancellationToken ct) => throw new AssertFailedException("The progressive UI must not request a whole-graph snapshot.");
        public Task<IReadOnlyList<SystemView>> ListAsync(ResourceScopeRef context, ResourceScopeRef owner, ResourceNamespace ns, CancellationToken ct) =>
            HoldSystems ? Systems.Task.WaitAsync(ct) : Task.FromResult<IReadOnlyList<SystemView>>([]);
        public Task<SystemView> CreateAsync(ResourceScopeRef context, CreateSystemRequest value, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> RenameAsync(ResourceScopeRef context, SystemView value, string displayName, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> AddMemberAsync(ResourceScopeRef context, SystemView value, SystemMember member, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> RemoveMemberAsync(ResourceScopeRef context, SystemView value, Guid uid, CancellationToken ct) => throw new AssertFailedException();
        public Task DeleteAsync(ResourceScopeRef context, SystemView value, CancellationToken ct) => throw new AssertFailedException();
    }
}
