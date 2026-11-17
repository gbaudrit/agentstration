using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Tools;
using Agentstration.Web.Console;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemToolAccessApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task RecordedNativeToolSetPublicationUsesOnlySelectedMembersAndRetainsAuthorizedProvenance()
    {
        await using var factory = Factory(); var current = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>(); var store = factory.Services.GetRequiredService<IResourceStore>();
        var owner = ResourceScopeRef.Workspace(current.WorkspaceId); var ns = new ResourceNamespace("tool-access-summary");
        SystemGraphChainFixture seed; AgentRevision revision; ToolResource other; ToolSetVersionResource publication;
        using (scopes.PushSystem())
        {
            seed = await SystemGraphChainFixture.SeedAsync(store, owner, ns, default);
            other = (await store.PutExactAsync(owner, seed.Tool with { Uid = Guid.Empty, Metadata = new() { Name = "not-selected", Namespace = ns },
                Definition = seed.Tool.Definition with { DisplayName = "Unselected tool" } }, null, true, default)).Value;
            var first = (await store.ListExactAsync<ToolSetVersionResource>(owner, ToolResourceKinds.ToolSetVersion, 0, 200, default)).Single().Value;
            publication = (await store.CreateImmutableAsync(first with { Uid = Guid.Empty, Metadata = new() { Name = "selection-v2", Namespace = ns },
                Version = "2.0.0", DefinitionHash = "selection-hash", Members = [first.Members[0], first.Members[0] with
                { ToolUid = other.Uid, ToolName = other.Name, Route = "not-selected" }] }, default)).Value;
            revision = (await store.CreateImmutableAsync(seed.Revision with { Uid = Guid.Empty, Metadata = new() { Name = "selected-agent-v2", Namespace = ns },
                AgentVersion = 2, Generation = 2, DefinitionHash = "agent-v2", Definition = seed.Revision.Definition with
                { ToolSetAssignments = [new() { ToolSetUid = publication.Uid, ToolSetScopeRef = owner, ToolSetNamespace = ns, ToolSetName = seed.ToolSet.Name,
                    ToolSetGeneration = publication.Generation, Version = publication.Version, DefinitionHash = publication.DefinitionHash,
                    Members = [ToolResourceIdentity.CatalogId(seed.Tool.Namespace, seed.Tool.Name)] }] } }, default)).Value;
        }
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        var root = SystemGraphIds.Definition(revision.Uid);
        var raw = await client.GetStringAsync("/api/system-graph/stream?namespace=" + ns.Value + "&root=" + Uri.EscapeDataString(root));
        var frames = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<SystemGraphStreamFrame>(line, JsonSerializerOptions.Web)!).ToArray();
        Assert.AreEqual("complete", frames[^1].Kind);
        var buffer = new SystemGraphStreamBuffer(); foreach (var frame in frames) Assert.IsTrue(buffer.Apply(frame));
        var graph = buffer.Page!; var grants = graph.Relations.Where(edge => edge.Source == root && edge.Kind == "toolset-access").ToArray();
        Assert.HasCount(1, grants); Assert.AreEqual(SystemGraphIds.Generation(seed.Tool.Uid, seed.Tool.Generation), grants[0].Target);
        Assert.AreEqual(SystemGraphIds.Publication(seed.ToolSet.Uid, publication.Version), grants[0].ViaNodeId);
        Assert.IsTrue(graph.Nodes.Any(node => node.Identity.ResourceUid == other.Uid), "The authorized technical inventory may show unselected publication members.");
        var summary = SystemExplorerSummaries.Create(graph, graph, root);
        Assert.IsFalse(summary.Graph.Nodes.Any(node => node.Identity.ResourceUid == other.Uid));
        var access = summary.Links.Single(link => link.Source.Id == root && link.Target.Identity.Kind == "Tool");
        Assert.AreEqual(publication.Version, access.Paths.Single(path => path.Kind == "toolset").Via!.Evidence!.Publication);
        Assert.IsTrue(access.ToolDefinitions.Any(node => node.Evidence?.Publication == "1.0.0"));
        Assert.DoesNotContain("private-agent-instructions", raw, StringComparison.Ordinal);
    }
}
