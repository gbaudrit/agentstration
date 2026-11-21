using System.Net;
using Agentstration.Reconciliation.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ReconciliationPreviewTests
{
    [TestMethod]
    [DataRow("en-US", "Reconciliation preview", "Inspect affected consumers", "Build review", "Revalidate review")]
    [DataRow("fr-FR", "Prévisualisation de réconciliation", "Examiner les consommateurs concernés", "Préparer la revue", "Revalider la revue")]
    public async Task ReviewIsExplicitLocalizedAndClearedAfterAStaleRevalidation(string culture, string title, string inspect, string build, string revalidate)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var context = new BunitContext();
        var ports = new Ports(); Register(context, ports);
        var page = context.Render<Agentstration.Web.Components.Pages.SystemReconciliation>();
        page.WaitForElement("select");
        Assert.Contains(title, page.Markup);
        await page.FindAll("select")[0].ChangeAsync("old");
        await page.FindAll("select")[1].ChangeAsync("current");
        await page.FindAll("button").Single(button => button.TextContent == inspect).ClickAsync();
        page.WaitForElement("input[type=checkbox]");
        Assert.IsNull(ports.Request);
        await page.Find("input[type=checkbox]").ChangeAsync(true);
        await page.FindAll("button").Single(button => button.TextContent == build).ClickAsync();
        page.WaitForElement("[data-testid=reconciliation-review]");
        Assert.IsNotNull(ports.Request);
        CollectionAssert.AreEqual(new[] { "agent-v1" }, ports.Request.Consumers.ToArray());
        Assert.AreEqual("old", ports.Request.From);
        Assert.AreEqual("current", ports.Request.To);
        Assert.HasCount(1, page.FindAll("[data-testid=reconciliation-review] button"), "A read-only review exposes no execution control.");
        ports.RejectRevalidation = true;
        await page.FindAll("button").Single(button => button.TextContent == revalidate).ClickAsync();
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindAll("[data-testid=reconciliation-review]")));
        Assert.DoesNotContain("private-error-detail", page.Markup);
    }

    [TestMethod]
    public async Task ChangingVersionWhilePreviewIsPendingDiscardsTheOldResponse()
    {
        using var context = new BunitContext();
        var ports = new Ports { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) }; Register(context, ports);
        var page = context.Render<Agentstration.Web.Components.Pages.SystemReconciliation>();
        page.WaitForElement("select");
        await page.FindAll("select")[0].ChangeAsync("old");
        await page.FindAll("select")[1].ChangeAsync("current");
        await page.Find("button[type=submit]").ClickAsync();
        page.WaitForElement("input[type=checkbox]");
        await page.Find("input[type=checkbox]").ChangeAsync(true);
        var click = page.Find(".button-primary").ClickAsync();
        page.WaitForAssertion(() => Assert.IsNotNull(ports.Request));
        await page.Find(".recon-consumer input[maxlength]").ChangeAsync("2.0.0");
        ports.Pending.SetResult(ports.Review);
        await click;
        Assert.IsEmpty(page.FindAll("[data-testid=reconciliation-review]"));
    }
    private static void Register(BunitContext context, Ports ports)
    {
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISystemExplorerClient>(ports);
        context.Services.AddSingleton<IReconciliationClient>(ports);
        context.Services.AddSingleton<IConsoleContextProvider>(ports);
        context.Services.AddSingleton<IConsoleNavigationContext>(ports);
    }
    private sealed class Ports : ISystemExplorerClient, IReconciliationClient, IConsoleContextProvider, IConsoleNavigationContext
    {
        private readonly ResourceScopeRef owner = ResourceScopeRef.Workspace(Guid.NewGuid());
        public ReconciliationPreviewRequest? Request { get; private set; }
        public bool RejectRevalidation { get; set; }
        public TaskCompletionSource<ReconciliationPreview>? Pending { get; set; }
        public string? ScopeRef => owner.Value;
        public string AddToUri(string uri) => uri;
        private SystemGraphNode Node(string id, string kind) => new(id, new(Guid.NewGuid(), owner, ResourceNamespace.Default, kind, id), "publication", new(1, Publication: "1"));
        public ReconciliationPreview Review => new("digest", DateTimeOffset.UnixEpoch, "generation-drift", [], [],
            [new("agent-v1", Node("agent-v1", "AgentRevision").Identity, "adopt", "blocked", "agent-adoption-policy-unavailable", null, [], [])],
            ["partial-coverage", "execution-not-available"], false, false);
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Operator", owner.Value, ConsoleContextKind.Workspace, Guid.NewGuid(), "tenant", "Tenant", Guid.NewGuid(), "workspace", "Workspace", new HashSet<string> { "resources/read" }, [], []));
        public Task<SystemGraphPage> ReadAsync(ResourceScopeRef context, ResourceNamespace ns, string? root, string? cursor, CancellationToken ct) =>
            Task.FromResult(new SystemGraphPage([Node("old", "ModelProfile"), Node("current", "ModelProfile")], [], "snapshot", DateTimeOffset.UnixEpoch, "partial", false, false, null));
        public Task<SystemGraphChangeResult> CompareAsync(ResourceScopeRef context, ResourceNamespace ns, string from, string to, CancellationToken ct)
        {
            var agent = Node("agent-v1", "AgentRevision");
            var result = new SystemGraphImpactResult([agent], [], "snapshot", DateTimeOffset.UnixEpoch, false, false, false, ["partial"]);
            return Task.FromResult(new SystemGraphChangeResult("generation-drift", result, result));
        }
        public Task<ReconciliationPreview> PreviewAsync(ResourceScopeRef context, ReconciliationPreviewRequest request, CancellationToken ct)
        { Request = request; return Pending?.Task ?? Task.FromResult(Review); }
        public Task<ReconciliationPreview> RevalidateAsync(ResourceScopeRef context, ReconciliationRevalidationRequest request, CancellationToken ct) =>
            RejectRevalidation ? Task.FromException<ReconciliationPreview>(new AgentstrationApiException("private-error-detail", "correlation", HttpStatusCode.Conflict)) : Task.FromResult(Review);
        public Task<IReadOnlyList<SystemView>> ListAsync(ResourceScopeRef context, ResourceScopeRef ownerScope, ResourceNamespace ns, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> CreateAsync(ResourceScopeRef context, CreateSystemRequest value, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> RenameAsync(ResourceScopeRef context, SystemView value, string displayName, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> AddMemberAsync(ResourceScopeRef context, SystemView value, SystemMember member, CancellationToken ct) => throw new AssertFailedException();
        public Task<SystemView> RemoveMemberAsync(ResourceScopeRef context, SystemView value, Guid uid, CancellationToken ct) => throw new AssertFailedException();
        public Task DeleteAsync(ResourceScopeRef context, SystemView value, CancellationToken ct) => throw new AssertFailedException();
    }
}
