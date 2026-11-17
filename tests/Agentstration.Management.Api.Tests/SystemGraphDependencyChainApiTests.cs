using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemGraphDependencyChainApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task AgentAndRevisionExposeTheNativeKnowledgeChainAndReadablePairedProviderWithoutRebindingARecreatedSource()
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var owner = ResourceScopeRef.Workspace(current.WorkspaceId); var ns = new ResourceNamespace("dependency-chain");
        SystemGraphChainFixture seed;
        using (scopes.PushSystem()) seed = await SystemGraphChainFixture.SeedAsync(store, owner, ns, default);
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        async Task<SystemGraphStreamFrame[]> Read(string root)
        {
            using var response = await client.GetAsync("/api/system-graph/stream?namespace=" + ns.Value + "&root=" + Uri.EscapeDataString(root),
                HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            await using var body = await response.Content.ReadAsStreamAsync(lifetime.Token);
            using var reader = new StreamReader(body); var frames = new List<SystemGraphStreamFrame>();
            while (await reader.ReadLineAsync(lifetime.Token) is { } line) frames.Add(JsonSerializer.Deserialize<SystemGraphStreamFrame>(line, JsonSerializerOptions.Web)!);
            Assert.AreEqual("complete", frames[^1].Kind); Assert.IsFalse(frames[^1].Truncated);
            return frames.ToArray();
        }
        foreach (var root in new[] { SystemGraphIds.Definition(seed.Agent.Uid), SystemGraphIds.Definition(seed.Revision.Uid), SystemGraphIds.Publication(seed.ToolSet.Uid, "1.0.0") })
        {
            var frames = await Read(root); var nodes = frames.SelectMany(frame => frame.Nodes).ToArray();
            foreach (var uid in new[] { seed.Tool.Uid, seed.Knowledge.Uid, seed.DataSource.Uid, seed.Service.Uid })
                Assert.IsTrue(nodes.Any(node => node.Id == SystemGraphIds.Definition(uid)), "Missing chain endpoint: " + uid);
            Assert.AreEqual("Readable extension", nodes.Single(node => node.Id == SystemGraphIds.Definition(seed.Provider.Uid)).DisplayName);
            var edge = frames.SelectMany(frame => frame.Relations).Single(edge => edge.Source == SystemGraphIds.Publication(seed.Relay.DefinitionUid, "1.0.0")
                && edge.Target == SystemGraphIds.Definition(seed.Knowledge.Uid));
            Assert.AreEqual("dynamic-dependency", edge.Kind); Assert.AreEqual("fixed-source-identity", edge.Resolution);
            var json = JsonSerializer.Serialize(frames);
            foreach (var privateValue in new[] { "private-connection-value", "private-agent-instructions" })
                Assert.DoesNotContain(privateValue, json, StringComparison.Ordinal);
            if (root != SystemGraphIds.Publication(seed.ToolSet.Uid, "1.0.0"))
                Assert.DoesNotContain("unrelated-peer-agent", json, StringComparison.Ordinal);
            else Assert.Contains("unrelated-peer-agent", json, StringComparison.Ordinal, "A directly selected ToolSet retains its own direct consumers.");
        }
        var strict = await client.GetFromJsonAsync<SystemGraphPage>("/api/system-graph?namespace=" + ns.Value + "&root=" + Uri.EscapeDataString(SystemGraphIds.Definition(seed.Agent.Uid)), lifetime.Token);
        Assert.IsFalse(strict!.Nodes.Any(node => node.Identity.ResourceUid == seed.Service.Uid), "The strict neighbour API retains its one-hop contract.");
        using var invalid = await client.GetAsync("/api/system-graph/stream?maximumDepth=33", lifetime.Token);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        var relay = (RelayToolDefinitionProperties)seed.Relay.Definition;
        foreach (var (version, source) in new[]
        {
            ("bad-uid", JsonSerializer.SerializeToElement(new { name = seed.Knowledge.Name, @namespace = ns.Value, scopeRef = owner.Value, uid = Guid.NewGuid() })),
            ("bad-namespace", JsonSerializer.SerializeToElement(new { name = seed.Knowledge.Name, @namespace = "wrong", scopeRef = owner.Value, uid = seed.Knowledge.Uid })),
            ("bad-encoding", JsonSerializer.SerializeToElement(new { name = seed.Knowledge.Name, @namespace = ns.Value, scopeRef = owner.Value, uid = 42 }))
        })
        {
            using (scopes.PushSystem())
                _ = await store.CreateImmutableAsync(seed.Relay with
                {
                    Uid = Guid.Empty, Metadata = seed.Relay.Metadata with { Name = "knowledge-access-" + version },
                    Definition = relay with { Version = version, Bindings = [new LiteralToolArgumentBinding { Path = "/source", Value = source }] }
                }, lifetime.Token);
            var rejected = await Read(SystemGraphIds.Publication(seed.Relay.DefinitionUid, version));
            Assert.IsFalse(rejected.SelectMany(frame => frame.Nodes).Any(node => node.Identity.Kind == KnowledgeResourceKinds.KnowledgeSource));
        }
        using (scopes.PushSystem())
        {
            await store.DeleteExactAsync(new(owner, ns, seed.Knowledge.Kind, seed.Knowledge.Name), seed.Knowledge.ETag, lifetime.Token);
            _ = await store.PutExactAsync(owner, seed.Knowledge with { Uid = Guid.Empty }, null, true, lifetime.Token);
        }
        var after = await Read(SystemGraphIds.Definition(seed.Agent.Uid));
        Assert.IsFalse(after.SelectMany(frame => frame.Nodes).Any(node => node.Identity.Kind == KnowledgeResourceKinds.KnowledgeSource));
        Assert.IsFalse(after.SelectMany(frame => frame.Nodes).Any(node => node.Identity.ResourceUid == seed.Service.Uid));
    }
}
