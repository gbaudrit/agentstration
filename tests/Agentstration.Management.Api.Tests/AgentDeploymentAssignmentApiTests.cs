using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AgentDeploymentAssignmentApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task DurableAssignmentsRetainExactConfigurationsWithoutRunsAndRejectStaleWritesAndForgedBindings()
    {
        await using var factory = Factory(); var current = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>(); var store = factory.Services.GetRequiredService<IResourceStore>();
        var owner = ResourceScopeRef.Workspace(current.WorkspaceId); var ns = new ResourceNamespace("deployment-photo");
        SystemGraphChainFixture seed; AgentRevision second;
        using (scopes.PushSystem())
        {
            seed = await SystemGraphChainFixture.SeedAsync(store, owner, ns, default);
            second = (await store.CreateImmutableAsync(seed.Revision with
            {
                Uid = Guid.Empty, Metadata = new() { Name = "consumer-agent-v2", Namespace = ns }, AgentVersion = 2, Generation = 2, DefinitionHash = "second-hash",
                Definition = seed.Revision.Definition with { AgentVersion = 2, DefinitionHash = "second-hash", ToolPins = [], ToolSetAssignments = [] }
            }, default)).Value;
            _ = await store.PutExactAsync(owner, seed.Agent with { Generation = 2 }, seed.Agent.ETag, false, default);
        }
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        var catalog = await client.GetFromJsonAsync<AgentDefinitionCatalog>("/api/agent-definition-catalog?uid=" + seed.Agent.Uid);
        Assert.AreEqual(second.Uid, catalog!.Revisions[0].Identity.ResourceUid); Assert.HasCount(2, catalog.Revisions);
        var deploymentNamespace = new ResourceNamespace("deployment-assignments");
        var request = new AgentDeploymentAssignmentRequest("production-agent", seed.Revision.Uid, "production",
            new("pool-a", "Pool A"), owner, deploymentNamespace);
        using var created = await client.PostAsJsonAsync("/api/agent-deployment-assignments/production-agent", request);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var first = (await created.Content.ReadFromJsonAsync<AgentDeploymentView>())!;
        Assert.HasCount(1, first.Configurations); Assert.AreEqual(seed.Revision.Uid, first.Configurations[0].RevisionUid);
        var changed = request with { RevisionUid = second.Uid, Target = new("pool-b", "Pool B") };
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/agent-deployment-assignments/production-agent") { Content = JsonContent.Create(changed) };
        update.Headers.IfMatch.ParseAdd(first.ETag);
        using var response = await client.SendAsync(update); Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var latest = (await response.Content.ReadFromJsonAsync<AgentDeploymentView>())!;
        Assert.AreEqual(first.Uid, latest.Uid); Assert.HasCount(2, latest.Configurations);
        Assert.AreEqual(first.Configurations[0], latest.Configurations[0]); Assert.AreEqual(second.Uid, latest.Configurations[1].RevisionUid);
        using var stale = new HttpRequestMessage(HttpMethod.Put, "/api/agent-deployment-assignments/production-agent") { Content = JsonContent.Create(changed) };
        stale.Headers.IfMatch.ParseAdd(first.ETag);
        using var rejected = await client.SendAsync(stale); Assert.AreEqual(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
        var listed = await client.GetFromJsonAsync<AgentDeploymentView[]>("/api/agent-deployment-assignments?namespace=" + ns.Value + "&agentUid=" + seed.Agent.Uid);
        Assert.HasCount(2, listed!.Single().Configurations);
        Assert.IsEmpty((await client.GetFromJsonAsync<AgentDeploymentView[]>("/api/agent-deployment-assignments?namespace=" + ns.Value))!);
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var instanceList = await client.GetFromJsonAsync<AgentDeploymentView[]>("/api/agent-deployment-assignments?namespace=" + ns.Value + "&agentUid=" + seed.Agent.Uid);
        Assert.AreEqual(latest.Uid, instanceList!.Single().Uid);
        SelectConsoleContext(client, owner);
        var root = $"deployment:{latest.Uid:D}:{first.Configurations[0].Id:D}";
        var graph = await client.GetFromJsonAsync<SystemGraphPage>("/api/system-graph?namespace=" + deploymentNamespace.Value + "&root=" + Uri.EscapeDataString(root));
        Assert.AreEqual(seed.Revision.Uid, graph!.Nodes.Single(node => node.Id == root).Deployment!.RevisionUid);
        Assert.IsFalse(graph.Nodes.Any(node => node.Identity.ResourceUid == second.Uid));
        var reference = new RuntimeAgentReference(seed.Agent.Name, 2)
        {
            Namespace = ns, ScopeRef = owner,
            Deployment = new(latest.Uid, latest.Configurations[1].Id, owner, deploymentNamespace, latest.Name)
        };
        using (scopes.PushSystem())
        {
            var resolver = factory.Services.GetRequiredService<IRuntimeAgentResolver>();
            var resolved = await resolver.ResolveAsync(reference, default);
            Assert.AreEqual(reference.Deployment, resolved.Deployment); Assert.AreEqual(second.Uid, resolved.RevisionUid);
            await Assert.ThrowsExactlyAsync<RuntimeAgentResolutionException>(() => resolver.ResolveAsync(reference with
            { Deployment = reference.Deployment with { ConfigurationId = first.Configurations[0].Id } }, default));
            await Assert.ThrowsExactlyAsync<RuntimeAgentResolutionException>(() => resolver.ResolveAsync(reference with
            { Deployment = reference.Deployment with { Uid = Guid.NewGuid() } }, default));
        }
        var historical = await client.GetFromJsonAsync<SystemOperationalSnapshot>("/api/system-operations?namespace=" + deploymentNamespace.Value + "&root=" + Uri.EscapeDataString(root));
        Assert.IsNotNull(historical!.Facts); Assert.IsNull(historical.Facts.Metrics); Assert.IsEmpty(historical.Facts.Runs);
        using var cross = await client.PostAsJsonAsync("/api/agent-deployment-assignments/foreign", request with { Name = "foreign", ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) });
        Assert.AreEqual(HttpStatusCode.Forbidden, cross.StatusCode);
        var raw = JsonSerializer.Serialize(listed); Assert.DoesNotContain("private-agent-instructions", raw, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecordedRunAttributionSelectsOnlyTheExactDeploymentConfigurationBeforeTheLimit()
    {
        await using var factory = Factory(); var current = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>(); var store = factory.Services.GetRequiredService<IResourceStore>();
        var runs = factory.Services.GetRequiredService<IRuntimeRunStore>();
        var owner = ResourceScopeRef.Workspace(current.WorkspaceId); var ns = new ResourceNamespace("deployment-observed");
        SystemGraphChainFixture seed;
        using (scopes.PushSystem()) seed = await SystemGraphChainFixture.SeedAsync(store, owner, ns, default);
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        using var created = await client.PostAsJsonAsync("/api/agent-deployment-assignments/observed-agent", new AgentDeploymentAssignmentRequest(
            "observed-agent", seed.Revision.Uid, "production", new("pool", "Pool"), owner, ns));
        created.EnsureSuccessStatusCode(); var deployment = (await created.Content.ReadFromJsonAsync<AgentDeploymentView>())!;
        var config = deployment.Configurations.Single(); var clock = factory.Services.GetRequiredService<TimeProvider>();
        var scope = RuntimeRunScope.Workspace(current.TenantId, new(current.WorkspaceId), current.PrincipalId);
        var run = new RuntimeRun
        {
            Id = "recorded-deployment-run", Name = "recorded-deployment-run", Scope = scope, WorkspaceId = scope.WorkspaceId,
            Properties = new() { Scope = scope, Agent = new(seed.Agent.Name, 1)
            { ResourceUid = seed.Agent.Uid, RevisionUid = seed.Revision.Uid, Namespace = ns, ScopeRef = owner, DefinitionHash = seed.Revision.DefinitionHash,
                Deployment = new(deployment.Uid, config.Id, owner, ns, deployment.Name) },
                Input = new() { Messages = [new(RuntimeMessageRole.User, "private-run-input")] }, Execution = new() },
            Status = new() { State = RuntimeRunState.Succeeded, CreatedAt = clock.GetUtcNow().AddSeconds(-10), StartedAt = clock.GetUtcNow().AddSeconds(-9), CompletedAt = clock.GetUtcNow().AddSeconds(-8) }
        };
        await runs.CreateAsync(run, default);
        for (var i = 0; i < 4; i++) await runs.CreateAsync(run with
        {
            Id = "newer-other-config-" + i, Name = "newer-other-config-" + i,
            Properties = run.Properties with { Agent = run.Properties.Agent with { Deployment = run.Properties.Agent.Deployment! with { ConfigurationId = Guid.NewGuid() } } },
            Status = run.Status with { CreatedAt = clock.GetUtcNow() }
        }, default);
        var root = $"deployment:{deployment.Uid:D}:{config.Id:D}";
        var snapshot = await client.GetFromJsonAsync<SystemOperationalSnapshot>("/api/system-operations?maximumRuns=1&namespace=" + ns.Value + "&root=" + Uri.EscapeDataString(root));
        Assert.IsNotNull(snapshot!.Facts); Assert.AreEqual("recorded-deployment-run", snapshot.Facts.Runs.Single().RunId);
        Assert.AreEqual(config.Id, snapshot.Facts.Runs[0].DeploymentConfigurationId); Assert.AreEqual(1, snapshot.Facts.Metrics!.ObservedSucceeded);
        Assert.DoesNotContain("private-run-input", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }
}
