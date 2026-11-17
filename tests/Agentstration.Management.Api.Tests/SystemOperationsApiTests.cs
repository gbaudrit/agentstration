using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemOperationsApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SqliteOverlayAndPrivateOrderedStreamUseNativeIdentityAndClearDeletedRoot()
    {
        await using var factory = Factory();
        var current = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        var runs = factory.Services.GetRequiredService<IRuntimeRunStore>();
        var owner = ResourceScopeRef.Workspace(current.WorkspaceId);
        var ns = ResourceNamespace.Parse("operations-fixture");
        StoredResource<AgentResource> agent;
        using (scopes.PushSystem())
        {
            agent = await resources.PutExactAsync(owner, new AgentResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.Agent,
                ScopeRef = owner,
                Metadata = new() { Name = "same-name", Namespace = ns },
                Generation = 1,
                Definition = new() { DisplayName = "Safe", Instructions = "private-instructions", ModelProfile = new("metadata-only") }
            }, null, true, default);
            var scope = RuntimeRunScope.Workspace(current.TenantId, new(current.WorkspaceId), current.PrincipalId);
            var run = new RuntimeRun
            {
                Id = "attested-run",
                Name = "attested-run",
                WorkspaceId = scope.WorkspaceId,
                Scope = scope,
                Properties = new()
                {
                    Agent = new("same-name", 1) { ResourceUid = agent.Value.Uid, ScopeRef = owner, Namespace = ns },
                    Scope = scope,
                    Input = new() { Messages = [new(RuntimeMessageRole.User, "private-input")] },
                    Execution = new()
                },
                Status = new()
                {
                    State = RuntimeRunState.Succeeded,
                    CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                    StartedAt = DateTimeOffset.UtcNow.AddSeconds(-4),
                    CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                }
            };
            await runs.CreateAsync(run, default);
            await runs.CreateAsync(run with
            {
                Id = "homonym-run",
                Name = "homonym-run",
                Properties = run.Properties with
                { Agent = run.Properties.Agent with { ResourceUid = Guid.NewGuid() } }
            }, default);
        }
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        var root = SystemGraphIds.Definition(agent.Value.Uid);
        var path = $"/api/system-operations?root={Uri.EscapeDataString(root)}&namespace={ns.Value}";
        using (var response = await client.GetAsync(path))
        {
            response.EnsureSuccessStatusCode();
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-input", json, StringComparison.Ordinal);
            Assert.DoesNotContain("private-instructions", json, StringComparison.Ordinal);
            var snapshot = JsonSerializer.Deserialize<SystemOperationalSnapshot>(json, JsonSerializerOptions.Web)!;
            Assert.IsNotNull(snapshot.Facts);
            CollectionAssert.AreEqual(new[] { "attested-run" }, snapshot.Facts.Runs.Select(run => run.RunId).ToArray());
            Assert.AreEqual(owner, snapshot.Facts.DataScope);
            Assert.AreEqual(1, snapshot.Facts.Metrics!.ObservedSucceeded);
        }
        using (var invalid = await client.GetAsync(path + "&maximumRuns=101")) Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        await using var connection = new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress!, "/hubs/system-operations?context=" + Uri.EscapeDataString(owner.Value)),
            options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers["X-Agentstration-Context"] = owner.Value;
            }).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.StartAsync(deadline.Token);
        await using var stream = connection.StreamAsync<SystemOperationsFrame>("WatchAsync", new SystemOperationsQuery(root, ns), deadline.Token).GetAsyncEnumerator(deadline.Token);
        Assert.IsTrue(await stream.MoveNextAsync());
        var first = stream.Current;
        Assert.AreEqual("snapshot", first.Kind); Assert.AreEqual(1L, first.Sequence);
        Assert.AreEqual(root, first.Upsert!.NodeId);
        using (scopes.PushSystem())
            await resources.DeleteExactAsync(new(owner, ns, AgentResourceKinds.Agent, "same-name"), agent.ETag, default);
        Assert.IsTrue(await stream.MoveNextAsync());
        Assert.AreEqual(first.Epoch, stream.Current.Epoch);
        Assert.AreEqual(2L, stream.Current.Sequence);
        Assert.IsTrue(stream.Current.Clear); Assert.IsNull(stream.Current.Upsert);
        using var missing = await client.GetAsync(path);
        missing.EnsureSuccessStatusCode();
        Assert.IsNull((await missing.Content.ReadFromJsonAsync<SystemOperationalSnapshot>())!.Facts);
    }
}
