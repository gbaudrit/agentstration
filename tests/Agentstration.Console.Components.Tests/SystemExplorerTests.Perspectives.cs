using Bunit;
using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;
using Agentstration.Web.SystemExplorer;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class SystemExplorerTests
{
    [TestMethod]
    [DataRow("en-US", "Compare with", "Read observed placements")]
    [DataRow("fr-FR", "Comparer avec", "Lire les placements observés")]
    public async Task ExplicitDefinitionPhotosAndDeploymentHistoryKeepExactRootsAndUnknownPlacement(string culture, string comparison, string observe)
    {
        using var locale = new TestCultureScope(culture);
        var client = new StreamClient(); var agent = StreamNode(client.Owner, "agent");
        var v1 = StreamNode(client.Owner, "revision-1") with { State = "publication", Evidence = new(Publication: "1") };
        var v2 = StreamNode(client.Owner, "revision-2") with { State = "publication", Evidence = new(Publication: "2") };
        client.Catalog = new(agent, [v2, v1]);
        client.Photo = new([agent, v1, v2], [], "photo", DateTimeOffset.UnixEpoch, "partial", false, false, null);
        using var context = StreamContext(client);
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/system-explorer?root={Uri.EscapeDataString(agent.Id)}&view=definition&revision={Uri.EscapeDataString(v1.Id)}&compare={Uri.EscapeDataString(v2.Id)}");
        var page = context.Render<Agentstration.Web.Components.Pages.SystemExplorer>();
        page.WaitForAssertion(() => Assert.HasCount(2, page.FindAll("[data-testid=explorer-node]")));
        CollectionAssert.AreEqual(new[] { v1.Id, v2.Id }, client.Roots.ToArray());
        Assert.Contains(comparison, page.Markup);
        Assert.DoesNotContain(">agent<", string.Concat(page.FindAll("[data-testid=explorer-node]").Select(row => row.OuterHtml)), StringComparison.Ordinal);
        var deploymentUid = Guid.NewGuid();
        AgentDeploymentConfiguration Config(Guid id, SystemGraphNode revision) => new(id, agent.Identity.ResourceUid, revision.Identity.ResourceUid,
            client.Owner, ResourceNamespace.Default, revision.Identity.Name, long.Parse(revision.Evidence!.Publication!, System.Globalization.CultureInfo.InvariantCulture), "hash", "production",
            new("pool", "Declared pool"), DateTimeOffset.UnixEpoch);
        var old = Config(Guid.NewGuid(), v1); var current = Config(Guid.NewGuid(), v2);
        client.Deployments = [new(deploymentUid, client.Owner, ResourceNamespace.Default, "production", "etag", current.Id, [old, current], "recorded", DateTimeOffset.UnixEpoch)];
        var graphId = $"deployment:{deploymentUid:D}:{old.Id:D}";
        var root = new SystemGraphNode(graphId, new(deploymentUid, client.Owner, ResourceNamespace.Default, "AgentDeployment", "production"), "recorded-deployment-configuration")
        { Deployment = new(deploymentUid, old.Id, old.AgentUid, old.RevisionUid, old.RevisionOwner, old.RevisionNamespace, old.RevisionName, 1, "hash", "production", "pool", "Declared pool", "worker-pool", "awp-worker", old.RecordedAt) };
        client.Photo = client.Photo with { Nodes = [root, v1], Relations = [] };
        await page.InvokeAsync(() => nav.NavigateTo($"/system-explorer?root={Uri.EscapeDataString(agent.Id)}&view=deployment&deployment={deploymentUid:D}&configuration={old.Id:D}"));
        page.WaitForAssertion(() => Assert.AreEqual(graphId, client.Roots.Last()));
        page.WaitForAssertion(() => Assert.AreEqual("deployment", page.FindComponent<SystemGraphDiagram>().Instance.Options!.Mode));
        Assert.Contains("Declared pool", page.Markup);
        Assert.IsEmpty(page.FindAll("form input[required]"), "Read-only users cannot write assignments.");
        await page.FindAll("button").Single(button => button.TextContent == observe).ClickAsync();
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindComponents<SystemGraphDiagram>()));
        Assert.HasCount(2, page.FindAll("[data-testid=explorer-node]"));
        Assert.IsFalse(context.JSInterop.Invocations.Where(call => call.Identifier == "render").Any(call =>
            call.Arguments.LastOrDefault() is SystemGraphDiagramOptions { Mode: "observed", Placements.Count: > 0 }));
        var attempt = new SystemWorkerEvidence(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "succeeded",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(10), DateTimeOffset.UnixEpoch, "unknown");
        var run = new SystemRunEvidence("runtime", "exact-run", client.Owner, client.Owner, client.Owner, ResourceNamespace.Default,
            agent.Identity.ResourceUid, old.RevisionUid, "1", "hash", "succeeded", DateTimeOffset.UnixEpoch, null, null,
            null, null, "unavailable", [new(Guid.NewGuid(), "succeeded", [attempt])])
            { DeploymentUid = deploymentUid, DeploymentConfigurationId = old.Id };
        var facts = new SystemOperationalFacts(graphId, root.Identity, root.State, "succeeded", "unknown", "unknown", null,
            [run, run with { RunId = "wrong-config", DeploymentConfigurationId = current.Id }], client.Owner, client.Owner,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "bounded-sample", false, []);
        client.Observation = new(facts, DateTimeOffset.UnixEpoch, "bounded-sample");
        await page.FindAll("button").Single(button => button.TextContent == observe).ClickAsync();
        Assert.IsEmpty(page.FindComponents<SystemGraphDiagram>(), "Run access alone cannot display Worker details.");
        client.Observation = client.Observation with { Facts = facts with { CanInspectWorkerDetails = true } };
        await page.FindAll("button").Single(button => button.TextContent == observe).ClickAsync();
        page.WaitForAssertion(() =>
        {
            var options = page.FindComponent<SystemGraphDiagram>().Instance.Options!;
            Assert.AreEqual("observed", options.Mode); Assert.HasCount(1, options.Placements!);
            Assert.AreEqual(attempt.SessionId, options.Placements![0].SessionId);
        });
    }

    [TestMethod]
    public async Task ACancelledCatalogCannotRestoreAnOldDefinitionAfterContextChanges()
    {
        var client = new StreamClient(); var agent = StreamNode(client.Owner, "private-old-agent");
        client.HeldCatalog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Photo = new([], [], "empty", DateTimeOffset.UnixEpoch, "partial", false, false, null);
        using var context = StreamContext(client); var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/system-explorer?root={Uri.EscapeDataString(agent.Id)}&view=definition");
        var page = context.Render<Agentstration.Web.Components.Pages.SystemExplorer>();
        var other = ResourceScopeRef.Workspace(Guid.NewGuid());
        await page.InvokeAsync(() => nav.NavigateTo("/system-explorer?context=" + Uri.EscapeDataString(other.Value)));
        client.HeldCatalog.SetResult(new(agent, []));
        page.WaitForAssertion(() => Assert.Contains(other.Value, page.Markup));
        Assert.DoesNotContain("private-old-agent", page.Markup, StringComparison.Ordinal);
    }
}
