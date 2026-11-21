using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class SystemExplorerTests
{
    [TestMethod]
    [DataRow("en-US", "System Explorer", "Open resource details")]
    [DataRow("fr-FR", "Explorateur de systèmes", "Ouvrir la fiche de la ressource")]
    public async Task NeutralListLocalizesAndKeepsHomonymsAndHostileLabelsDistinct(string culture, string title, string details)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        var client = new Client();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISystemExplorerClient>(client);
        context.Services.AddSingleton<ISystemOperationsClient>(new OperationsClient());
        context.Services.AddSingleton<IConsoleContextProvider>(new Provider(client.Owner));
        context.Services.AddSingleton<IConsoleNavigationContext>(new Navigation(client.Owner));
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.SystemExplorer/system-explorer.js");
        module.Setup<bool>("render", _ => true).SetResult(true);
        var page = context.Render<Agentstration.Web.Components.Pages.SystemExplorer>();
        page.WaitForElement("[data-testid=explorer-node]");
        Assert.Contains(title, page.Markup);
        Assert.HasCount(2, page.FindAll("[data-testid=explorer-node]"));
        Assert.IsEmpty(page.FindAll("img[src=x]"));
        await page.FindAll("[data-testid=explorer-node] button")[0].ClickAsync();
        Assert.AreEqual(page.Find("[data-testid=explorer-selection] p code").TextContent, page.FindComponent<SystemOperationsPanel>().Instance.Root);
        var first = page.Find("[data-testid=explorer-selection] a").GetAttribute("href")!;
        Assert.Contains("scopeRef=%2Finstance", first);
        Assert.Contains("context=%2Fworkspaces%2F", first);
        Assert.Contains(details, page.Find("[data-testid=explorer-selection] a").TextContent);
        await page.FindAll("[data-testid=explorer-node] button")[1].ClickAsync();
        var second = page.Find("[data-testid=explorer-selection] a").GetAttribute("href")!;
        Assert.AreNotEqual(first, second);
        Assert.IsEmpty(page.FindAll("form input[required]"), "Read-only operators cannot author systems.");
        await page.Find("input[type=checkbox]").ChangeAsync(false);
        Assert.HasCount(2, page.FindAll("[data-testid=explorer-node]"));
        Assert.IsEmpty(page.FindAll("[aria-label*='diagram']"));
    }

    private sealed class OperationsClient : ISystemOperationsClient
    {
        public IAsyncEnumerable<Agentstration.SystemOperations.Contracts.SystemOperationsFrame> WatchAsync(ResourceScopeRef context,
            Agentstration.SystemOperations.Contracts.SystemOperationsQuery query, CancellationToken ct) => throw new AssertFailedException("Overlay is opt-in.");
    }
    private sealed class Navigation(ResourceScopeRef owner) : IConsoleNavigationContext
    {
        public string? ScopeRef => owner.Value;
        public string AddToUri(string uri) => uri;
    }
    private sealed class Provider(ResourceScopeRef owner) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Operator", owner.Value, ConsoleContextKind.Workspace, Guid.NewGuid(), "tenant", "Tenant", Guid.NewGuid(), "workspace", "Workspace",
            new HashSet<string> { "resources/read" }, [], []));
    }
    private sealed class Client : ISystemExplorerClient
    {
        public ResourceScopeRef Owner { get; } = ResourceScopeRef.Workspace(Guid.NewGuid());
        public Task<SystemGraphPage> ReadAsync(ResourceScopeRef context, ResourceNamespace ns, string? root, string? cursor, CancellationToken ct)
        {
            Assert.AreEqual(Owner, context);
            var nodes = new[] { new SystemGraphNode("resource:instance", new(Guid.NewGuid(), ResourceScopeRef.Instance, ns, "ModelProfile", "same-name"), "draft") { DisplayName = "<img src=x onerror=alert(1)>" },
                new SystemGraphNode("resource:workspace", new(Guid.NewGuid(), Owner, ns, "ModelProfile", "same-name"), "draft") };
            return Task.FromResult(new SystemGraphPage(nodes, [], "snapshot", DateTimeOffset.UnixEpoch, "partial", false, false, null) { Scopes = [new(Owner, ResourceScopeRef.Instance), new(ResourceScopeRef.Instance, null)] });
        }
        public Task<IReadOnlyList<SystemView>> ListAsync(ResourceScopeRef context, ResourceScopeRef owner, ResourceNamespace ns, CancellationToken ct) => Task.FromResult<IReadOnlyList<SystemView>>([]);
        public Task<SystemView> CreateAsync(ResourceScopeRef context, CreateSystemRequest value, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> RenameAsync(ResourceScopeRef context, SystemView value, string displayName, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> AddMemberAsync(ResourceScopeRef context, SystemView value, SystemMember member, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> RemoveMemberAsync(ResourceScopeRef context, SystemView value, Guid uid, CancellationToken ct) => throw new AssertFailedException();
        public Task DeleteAsync(ResourceScopeRef context, SystemView value, CancellationToken ct) => throw new AssertFailedException();
    }
}
