using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Reconciliation.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ReconciliationPreviewApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SqlitePreviewWritesNothingRevalidatesRelevantEvidenceAndFiltersRevokedRoots()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        var flows = factory.Services.GetRequiredService<IFlowRepository>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        var sourceOwner = ResourceScopeRef.Instance;
        var ns = new ResourceNamespace("reconciliation-fixture");
        StoredResource<ModelProfileResource> profile;
        StoredResource<AgentRevision> revision;
        Guid flowUid;
        using (scopes.PushSystem())
        {
            var initial = await resources.PutExactAsync(sourceOwner, Profile("source", sourceOwner, ns), null, true, default);
            profile = await resources.PutExactAsync(sourceOwner, initial.Value with
            {
                Generation = 2,
                Definition = initial.Value.Definition with { Model = new() { Name = "new-model-generation" } }
            }, initial.ETag, false, default);
            var agent = await resources.PutExactAsync(owner, new AgentResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.Agent,
                ScopeRef = owner,
                Metadata = new() { Name = "consumer", Namespace = ns },
                Generation = 1,
                Definition = new() { DisplayName = "Consumer", Instructions = "private-instructions", ModelProfile = new("source", sourceOwner, ns) }
            }, null, true, default);
            revision = await resources.CreateImmutableAsync(new AgentRevision
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.AgentRevision,
                ScopeRef = owner,
                Metadata = new() { Name = "consumer-v1", Namespace = ns },
                Generation = 1,
                AgentUid = agent.Value.Uid,
                AgentName = "consumer",
                AgentNamespace = ns,
                AgentVersion = 1,
                DefinitionHash = "agent-hash",
                CreatedAt = DateTimeOffset.UnixEpoch,
                ProvisioningState = ProvisioningState.Succeeded,
                Definition = new()
                {
                    AgentId = agent.Value.Uid,
                    AgentKey = "consumer",
                    AgentVersion = 1,
                    DisplayName = "Consumer",
                    Description = "",
                    EffectiveInstructions = "private-instructions",
                    ModelProfileName = "source",
                    ModelProfileNamespace = ns,
                    ModelProfilePin = new(profile.Value.Uid, sourceOwner, ns, ModelResourceKinds.ModelProfile, "source", 1),
                    RuntimeProfileName = "maf-builtin",
                    EffectiveToolNames = [],
                    MiddlewareIds = [],
                    ContextProviderIds = [],
                    Capabilities = [],
                    Handler = "prompt-agent",
                    DefinitionHash = "agent-hash"
                }
            }, default);
            var target = new FlowTargetReference(FlowTargetKind.Agent, "consumer", "1", ns, owner, agent.Value.Uid, revision.Value.Uid, "agent-hash");
            var flow = await flows.CreateAsync(new FlowResource(owner, new FlowId("selected-flow", ns), "Selected flow", null, "1.0", true, "1.0", new DirectFlowDefinition(target), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), default);
            flowUid = flow.Value.Uid;
            await flows.CreateVersionAsync(new(owner, flow.Value.Id, "1.0", null, new DirectFlowDefinition(target), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DefinitionHash: "flow-hash"), default);
            await policies.PutAsync(new(new(profile.Value.Uid, sourceOwner, ns, ModelResourceKinds.ModelProfile), [new(owner, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        }
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        var request = new ReconciliationPreviewRequest(SystemGraphIds.Generation(profile.Value.Uid, 1), SystemGraphIds.Definition(profile.Value.Uid),
            [SystemGraphIds.Definition(revision.Value.Uid), SystemGraphIds.Publication(flowUid, "1.0")], ns,
            new Dictionary<string, string> { [SystemGraphIds.Publication(flowUid, "1.0")] = "2.0" });
        using var response = await client.PostAsJsonAsync("/api/system-reconciliation/preview", request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
        var preview = (await response.Content.ReadFromJsonAsync<ReconciliationPreview>())!;
        Assert.HasCount(2, preview.Steps);
        Assert.AreEqual("AgentRevision", preview.Steps[0].Identity.Kind);
        Assert.AreEqual("Flow", preview.Steps[1].Identity.Kind);
        Assert.IsFalse(preview.CanExecute); Assert.IsFalse(preview.GloballyComplete);
        Assert.DoesNotContain("private-instructions", JsonSerializer.Serialize(preview), StringComparison.Ordinal);
        Assert.Contains("agent-adoption-policy-unavailable", preview.Blockers);
        using (scopes.PushSystem())
        {
            Assert.AreEqual(profile.ETag, (await resources.GetExactAsync<ModelProfileResource>(new(sourceOwner, ns, ModelResourceKinds.ModelProfile, "source"), default))!.ETag);
            Assert.AreEqual(revision.ETag, (await resources.GetExactAsync<AgentRevision>(new(owner, ns, AgentResourceKinds.AgentRevision, "consumer-v1"), default))!.ETag);
            Assert.HasCount(1, await flows.ListVersionsExactAsync(owner, new FlowId("selected-flow", ns), default));
            await resources.PutExactAsync(owner, Profile("unrelated", owner, ns), null, true, default);
        }
        using (var unrelated = await client.PostAsJsonAsync("/api/system-reconciliation/revalidate", new ReconciliationRevalidationRequest(request, preview.Digest)))
            Assert.AreEqual(HttpStatusCode.OK, unrelated.StatusCode, await unrelated.Content.ReadAsStringAsync());
        using (scopes.PushSystem())
        {
            var currentAgent = (await resources.GetExactAsync<AgentResource>(new(owner, ns, AgentResourceKinds.Agent, "consumer"), default))!;
            await resources.PutExactAsync(owner, currentAgent.Value with { Generation = 2, Definition = currentAgent.Value.Definition with { Instructions = "changed-private-instructions" } }, currentAgent.ETag, false, default);
        }
        using (var changedConsumer = await client.PostAsJsonAsync("/api/system-reconciliation/revalidate", new ReconciliationRevalidationRequest(request, preview.Digest)))
            Assert.AreEqual(HttpStatusCode.Conflict, changedConsumer.StatusCode);
        using (var fresh = await client.PostAsJsonAsync("/api/system-reconciliation/preview", request))
        {
            Assert.AreEqual(HttpStatusCode.OK, fresh.StatusCode, await fresh.Content.ReadAsStringAsync());
            preview = (await fresh.Content.ReadFromJsonAsync<ReconciliationPreview>())!;
        }
        using (scopes.PushSystem())
            profile = await resources.PutExactAsync(sourceOwner, profile.Value with { Generation = 3, Definition = profile.Value.Definition with { Model = new() { Name = "changed-again" } } }, profile.ETag, false, default);
        using (var stale = await client.PostAsJsonAsync("/api/system-reconciliation/revalidate", new ReconciliationRevalidationRequest(request, preview.Digest)))
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        using (scopes.PushSystem())
        {
            var policy = (await policies.GetAsync(profile.Value.Uid, default))!;
            await policies.PutAsync(new(new(profile.Value.Uid, sourceOwner, ns, ModelResourceKinds.ModelProfile), []), policy.ETag, default);
        }
        using var hiddenResponse = await client.PostAsJsonAsync("/api/system-reconciliation/preview", request);
        var hidden = (await hiddenResponse.Content.ReadFromJsonAsync<ReconciliationPreview>())!;
        Assert.IsEmpty(hidden.ReadSet); Assert.IsEmpty(hidden.Steps); Assert.IsEmpty(hidden.Relations);
        Assert.DoesNotContain(revision.Value.Uid.ToString(), JsonSerializer.Serialize(hidden), StringComparison.Ordinal);
        Assert.DoesNotContain(flowUid.ToString(), JsonSerializer.Serialize(hidden), StringComparison.Ordinal);
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
}
