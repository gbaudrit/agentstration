using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemGraphStreamApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SqliteStreamFlushesBeforeSlowMetadataAndDeletionProducesAGenericClearingFrame()
    {
        var source = new SlowSource();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ISystemGraphContributor>(source)));
        var current = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var owner = ResourceScopeRef.Workspace(current.WorkspaceId); var ns = new ResourceNamespace("stream-fixture");
        StoredResource<ModelProfileResource> first;
        using (scopes.PushSystem())
        {
            first = await store.PutExactAsync(owner, Profile("stream-first", owner, ns), null, true, default);
            var second = await store.PutExactAsync(owner, Profile("stream-second", owner, ns), null, true, default);
            source.Owner = owner; source.Nodes = [ResourceSystemGraph.Node(first.Value), ResourceSystemGraph.Node(second.Value)];
        }
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system-graph/stream?namespace=" + ns.Value);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
            Assert.IsTrue(response.Headers.CacheControl!.NoStore);
            Assert.AreEqual("no", response.Headers.GetValues("X-Accel-Buffering").Single());
            await using var body = await response.Content.ReadAsStreamAsync(lifetime.Token);
            using var reader = new StreamReader(body);
            async Task<SystemGraphStreamFrame> Read() => JsonSerializer.Deserialize<SystemGraphStreamFrame>(
                (await reader.ReadLineAsync(lifetime.Token))!, JsonSerializerOptions.Web)!;
            Assert.AreEqual("start", (await Read()).Kind);
            var initial = await Read();
            Assert.AreEqual("append", initial.Kind); Assert.AreEqual(first.Value.Uid, initial.Nodes.Single().Identity.ResourceUid);
            await source.Waiting.Task.WaitAsync(lifetime.Token);
            Assert.IsFalse(source.Release.Task.IsCompleted, "First data must be readable before the family finishes.");
            Assert.AreEqual("heartbeat", (await Read()).Kind, "A pending trusted metadata read must not replace the actor context for heartbeats.");
            Assert.DoesNotContain("metadata-only", JsonSerializer.Serialize(initial), StringComparison.Ordinal);
            using (scopes.PushSystem())
                await store.DeleteExactAsync(ScopedResourceAddress.Create(owner, ns, first.Value.Kind, first.Value.Name), first.ETag, default);
            source.Release.TrySetResult();
            var tail = new List<SystemGraphStreamFrame>();
            while (await reader.ReadLineAsync(lifetime.Token) is { } line)
                tail.Add(JsonSerializer.Deserialize<SystemGraphStreamFrame>(line, JsonSerializerOptions.Web)!);
            Assert.AreEqual("error", tail[^1].Kind); Assert.AreEqual("system_graph_changed", tail[^1].ErrorCode);
            Assert.IsFalse(tail.Any(frame => frame.Kind == "complete"));
            Assert.DoesNotContain(first.Value.Uid.ToString(), JsonSerializer.Serialize(tail), StringComparison.Ordinal);
            using var invalid = await client.GetAsync("/api/system-graph/stream?maximumNodes=0", lifetime.Token);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally { source.Release.TrySetResult(); await lifetime.CancelAsync(); }
    }
    private static ModelProfileResource Profile(string name, ResourceScopeRef owner, ResourceNamespace ns) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ModelResourceKinds.ModelProfile,
        ScopeRef = owner,
        Metadata = new() { Name = name, Namespace = ns },
        Generation = 1,
        Definition = new() { DisplayName = name, Provider = new("metadata-only", ResourceScopeRef.Instance), Model = new() { Name = "deterministic" } }
    };
    private sealed class SlowSource : ISystemGraphContributor
    {
        public string Family => "aaa-progressive-fixture";
        public ResourceScopeRef Owner { get; set; }
        public SystemGraphNode[] Nodes { get; set; } = [];
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<SystemGraphContribution> ReadAsync(ResourceScopeRef scope, [EnumeratorCancellation] CancellationToken ct)
        {
            if (scope != Owner) yield break;
            yield return new([Nodes[0]], []);
            Waiting.TrySetResult(); await Release.Task.WaitAsync(ct);
            yield return new([Nodes[1]], []);
        }
    }
}
