using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Tools;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemGraphApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task ExactPublishedChainRebuildsFromSqliteWithoutCreatingResourcesOrRebindingHomonyms()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        var flows = factory.Services.GetRequiredService<IFlowRepository>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        var agentOwner = ResourceScopeRef.Tenant(context.TenantId);
        var ns = new ResourceNamespace("graph-fixture");
        StoredResource<ModelProfileResource> profile;
        StoredResource<ModelProfileResource> otherNamespaceProfile;
        StoredResource<AgentRevision> revision;
        Guid flowUid;
        Guid toolUid;
        Guid toolSetUid;
        using (scopes.PushSystem())
        {
            profile = await resources.PutExactAsync(ResourceScopeRef.Instance, Profile("same-name", ResourceScopeRef.Instance, ns), null, true, default);
            otherNamespaceProfile = await resources.PutExactAsync(owner, Profile("same-name", owner, new ResourceNamespace("graph-other")), null, true, default);
            var shadow = await resources.PutExactAsync(owner, Profile("same-name", owner, ns), null, true, default);
            var provider = await resources.PutExactAsync(agentOwner, new ToolProviderResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolProvider,
                ScopeRef = agentOwner,
                Metadata = new() { Name = "source-provider", Namespace = ns },
                Generation = 1,
                Definition = new() { DisplayName = "Source provider", ProviderType = ToolProviderType.Mcp }
            }, null, true, default);
            var tool = await resources.PutExactAsync(agentOwner, new ToolResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.Tool,
                ScopeRef = agentOwner,
                Metadata = new() { Name = "source-tool", Namespace = ns },
                Generation = 1,
                Definition = new() { DisplayName = "Source tool", Version = "1.0.0", Provider = new(provider.Value.Name, agentOwner, ns) }
            }, null, true, default);
            toolUid = tool.Value.Uid;
            var toolSet = await resources.PutExactAsync(agentOwner, new ToolSetResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolSet,
                ScopeRef = agentOwner,
                Metadata = new() { Name = "source-set", Namespace = ns },
                Generation = 1,
                Definition = new() { DisplayName = "Source set" }
            }, null, true, default);
            toolSetUid = toolSet.Value.Uid;
            await resources.CreateImmutableAsync(new ToolSetVersionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolSetVersion,
                ScopeRef = agentOwner,
                Metadata = new() { Name = "source-set-v1", Namespace = ns },
                Generation = 1,
                ToolSetUid = toolSetUid,
                ToolSetName = toolSet.Value.Name,
                ToolSetGeneration = 1,
                Version = "1.0.0",
                DefinitionHash = "toolset-hash",
                PublishedAt = DateTimeOffset.UnixEpoch,
                Members = [new() { Capability = "search", Route = "source-route", ToolName = tool.Value.Name, ToolNamespace = ns,
                    ToolScopeRef = agentOwner, ToolUid = toolUid, ToolGeneration = 1, ProviderName = provider.Value.Name,
                    ProviderNamespace = ns, ProviderScopeRef = agentOwner, ProviderUid = provider.Value.Uid, ProviderGeneration = 1,
                    ExternalToolId = "source", InputSchema = JsonSerializer.SerializeToElement(new { }) }]
            }, default);
            foreach (var resource in new Resource[] { provider.Value, tool.Value, toolSet.Value })
                await policies.PutAsync(new(new(resource.Uid, agentOwner, ns, resource.Kind), [new(owner, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
            var schema = await resources.PutExactAsync(agentOwner, new SchemaResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SchemaResourceKinds.Schema,
                ScopeRef = agentOwner,
                Metadata = new() { Name = "source-schema", Namespace = ns },
                Generation = 1,
                Definition = new()
                {
                    DisplayName = "Source schema",
                    Format = "json-schema",
                    Content = JsonSerializer.SerializeToElement(new { type = "object", description = "private-schema-body" })
                }
            }, null, true, default);
            await resources.CreateImmutableAsync(new SchemaVersionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SchemaResourceKinds.SchemaVersion,
                ScopeRef = agentOwner,
                Metadata = new() { Name = "source-schema-v1", Namespace = ns },
                Generation = 1,
                SchemaUid = schema.Value.Uid,
                SchemaName = schema.Value.Name,
                SchemaNamespace = ns,
                SchemaGeneration = 1,
                Version = "1.0.0",
                Format = "json-schema",
                Content = schema.Value.Definition.Content,
                DefinitionHash = "parent-hash",
                PublishedAt = DateTimeOffset.UnixEpoch
            }, default);
            await policies.PutAsync(new(new(schema.Value.Uid, agentOwner, ns, schema.Value.Kind),
                [new(owner, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
            var agent = await resources.PutExactAsync(agentOwner, new AgentResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.Agent,
                ScopeRef = agentOwner,
                Metadata = new() { Name = "consumer", Namespace = ns },
                Generation = 1,
                Definition = new() { DisplayName = "Consumer", Instructions = "deterministic", ModelProfile = new("same-name", ResourceScopeRef.Instance, ns) }
            }, null, true, default);
            revision = await resources.CreateImmutableAsync(new AgentRevision
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.AgentRevision,
                ScopeRef = agentOwner,
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
                    EffectiveInstructions = "deterministic",
                    ModelProfileName = "same-name",
                    ModelProfileNamespace = ns,
                    ModelProfilePin = new(profile.Value.Uid, ResourceScopeRef.Instance, ns, ModelResourceKinds.ModelProfile, "same-name", 1),
                    RuntimeProfileName = "maf-builtin",
                    SchemaBindings = new Dictionary<string, SchemaBindingSnapshot>
                    {
                        ["definition.inputSchema"] = new("definition.inputSchema", "json-schema", null,
                            JsonSerializer.SerializeToElement(new { type = "object", description = "private-schema-body" }), "selected-hash",
                            new() { Name = schema.Value.Name, Namespace = ns, ScopeRef = agentOwner, Version = "1.0.0", SchemaPath = "/properties/result" }, schema.Value.Uid, 1)
                        { ParentDefinitionHash = "parent-hash" }
                    },
                    ToolSetAssignments = [new() { ToolSetUid = toolSetUid, ToolSetScopeRef = agentOwner, ToolSetName = "source-set",
                        ToolSetNamespace = ns, ToolSetGeneration = 1, Version = "1.0.0", DefinitionHash = "toolset-hash", Members = ["source-route"] }],
                    EffectiveToolNames = [],
                    MiddlewareIds = [],
                    ContextProviderIds = [],
                    Capabilities = [],
                    Handler = "prompt-agent",
                    DefinitionHash = "agent-hash"
                }
            }, default);
            await resources.PutExactAsync(owner, agent.Value with
            {
                Uid = Guid.Empty,
                ScopeRef = owner,
                Definition = agent.Value.Definition with { ModelProfile = new("same-name", owner, ns) }
            }, null, true, default);
            await policies.PutAsync(new(new(agent.Value.Uid, agentOwner, ns, agent.Value.Kind), [new(owner, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
            var target = new FlowTargetReference(FlowTargetKind.Agent, "consumer", "1", ns, agentOwner, agent.Value.Uid, revision.Value.Uid, "agent-hash");
            var flow = await flows.CreateAsync(new FlowResource(owner, new FlowId("consumer-flow", ns), "consumer-flow", null, "1.0", true, "1.0", new DirectFlowDefinition(target), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), default);
            flowUid = flow.Value.Uid;
            await flows.CreateVersionAsync(new(owner, flow.Value.Id, "1.0", null, new DirectFlowDefinition(target), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DefinitionHash: "flow-hash"), default);
            await flows.CreateVersionAsync(new(owner, flow.Value.Id, "2.0", null, new DirectFlowDefinition(target), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch.AddDays(1), DefinitionHash: "flow-hash-v2"), default);
            await policies.PutAsync(new(new(profile.Value.Uid, ResourceScopeRef.Instance, ns, profile.Value.Kind),
                [new(owner, [ResourceAccessAction.Discover, ResourceAccessAction.Use]), new(agentOwner, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
            Assert.AreNotEqual(profile.Value.Uid, shadow.Value.Uid);
        }
        using var client = factory.CreateClient();
        SelectConsoleContext(client, owner);
        var before = await client.GetFromJsonAsync<SystemGraphPage>("/api/system-graph?namespace=graph-fixture");
        var after = await client.GetFromJsonAsync<SystemGraphPage>("/api/system-graph?namespace=graph-fixture");
        Assert.IsNotNull(before);
        Assert.IsNotNull(after);
        Assert.AreEqual(before.Snapshot, after.Snapshot);
        Assert.IsTrue(before.Nodes.All(node => node.Identity.Namespace == ns));
        var otherNamespace = (await client.GetFromJsonAsync<SystemGraphPage>("/api/system-graph?namespace=graph-other"))!;
        Assert.AreEqual(otherNamespaceProfile.Value.Uid, otherNamespace.Nodes.Single().Identity.ResourceUid);
        Assert.IsEmpty(otherNamespace.Relations);
        Assert.IsFalse(before.GloballyComplete);
        var agentRevisionId = SystemGraphIds.Definition(revision.Value.Uid);
        var modelGenerationId = SystemGraphIds.Generation(profile.Value.Uid, 1);
        Assert.IsTrue(before.Relations.Any(edge => edge.Source == agentRevisionId && edge.Target == modelGenerationId && edge.Kind == "exact-dependency"));
        var schemaEvidence = before.Relations.Single(edge => edge.Source == agentRevisionId && edge.SourceField == "definition.inputSchema");
        Assert.AreEqual("selected-hash", schemaEvidence.ExpectedHash);
        Assert.AreEqual("parent-hash", schemaEvidence.ParentHash);
        Assert.AreEqual("/properties/result", schemaEvidence.TargetField);
        Assert.AreEqual("1.0.0", schemaEvidence.SelectedPublication);
        Assert.DoesNotContain("private-schema-body", JsonSerializer.Serialize(before), StringComparison.Ordinal);
        Assert.IsTrue(before.Relations.Any(edge => edge.Source == SystemGraphIds.Publication(flowUid, "1.0") && edge.Target == agentRevisionId));
        Assert.IsTrue(before.Nodes.Any(node => node.Id == SystemGraphIds.Publication(flowUid, "2.0") && node.Evidence?.Hash == "flow-hash-v2"));
        var impact = (await client.GetFromJsonAsync<SystemGraphImpactResult>("/api/system-graph/impact?namespace=graph-fixture&root=" + Uri.EscapeDataString(SystemGraphIds.Definition(toolUid))))!;
        Assert.IsFalse(impact.GloballyComplete);
        var chain = impact.Paths.Single(path => path.Nodes[^1] == SystemGraphIds.Publication(flowUid, "1.0"));
        Assert.Contains(SystemGraphIds.Publication(toolSetUid, "1.0.0"), chain.Nodes);
        Assert.Contains(agentRevisionId, chain.Nodes);
        Assert.IsTrue(chain.Relations.Any(edge => edge.SourceField == "members[source-route].tool"));
        Assert.IsFalse(impact.Nodes.Any(node => node.Identity.ResourceUid == otherNamespaceProfile.Value.Uid));
        using (scopes.PushSystem())
        {
            Assert.AreEqual("flow-hash", (await flows.GetVersionExactAsync(owner, new("consumer-flow", ns), "1.0", default))!.Value.DefinitionHash);
            Assert.AreEqual(profile.ETag, (await resources.GetByUidAsync<ModelProfileResource>(profile.Value.Uid, default))!.ETag);
            Assert.AreEqual(revision.ETag, (await resources.GetByUidAsync<AgentRevision>(revision.Value.Uid, default))!.ETag);
            Assert.HasCount(2, await flows.ListVersionsExactAsync(owner, new("consumer-flow", ns), default));
            var firstVersionPage = await flows.ListVersionsPageExactAsync(owner, new("consumer-flow", ns), 0, 1, default);
            var secondVersionPage = await flows.ListVersionsPageExactAsync(owner, new("consumer-flow", ns), 1, 1, default);
            Assert.IsTrue(firstVersionPage.HasMore);
            Assert.IsFalse(secondVersionPage.HasMore);
            Assert.AreEqual("1.0", firstVersionPage.Items.Single().Value.Version);
            Assert.AreEqual("2.0", secondVersionPage.Items.Single().Value.Version);
            await resources.DeleteExactAsync(ScopedResourceAddress.Create(ResourceScopeRef.Instance, ns, profile.Value.Kind, profile.Value.Name), profile.ETag, default);
            var replacement = await resources.PutExactAsync(ResourceScopeRef.Instance, Profile("same-name", ResourceScopeRef.Instance, ns), null, true, default);
            Assert.AreNotEqual(profile.Value.Uid, replacement.Value.Uid);
        }
        var recreated = (await client.GetFromJsonAsync<SystemGraphPage>("/api/system-graph?namespace=graph-fixture"))!;
        Assert.IsFalse(recreated.Relations.Any(edge => edge.Source == agentRevisionId && edge.Target == modelGenerationId));
        Assert.IsFalse(recreated.Nodes.Any(node => node.Identity.ResourceUid == profile.Value.Uid));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task RealAccessOverlaySeparatesDiscoverUseAndRevocation(bool discover, bool use)
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        var ns = new ResourceNamespace("graph-permissions");
        StoredResource<ModelProfileResource> profile;
        using (scopes.PushSystem())
        {
            profile = await store.PutExactAsync(ResourceScopeRef.Instance, Profile("protected", ResourceScopeRef.Instance, ns), null, true, default);
            var actions = new List<ResourceAccessAction>();
            if (discover) actions.Add(ResourceAccessAction.Discover);
            if (use) actions.Add(ResourceAccessAction.Use);
            if (actions.Count > 0) await policies.PutAsync(new(new(profile.Value.Uid, ResourceScopeRef.Instance, ns, profile.Value.Kind), [new(owner, actions)]), null, default);
        }
        using var client = factory.CreateClient();
        SelectConsoleContext(client, owner);
        var page = (await client.GetFromJsonAsync<SystemGraphPage>("/api/system-graph?namespace=graph-permissions"))!;
        Assert.AreEqual(discover ? 1 : 0, page.Nodes.Count);
        if (discover) Assert.AreEqual(use, page.Nodes[0].Evidence is not null);
        if (!discover) Assert.DoesNotContain(profile.Value.Uid.ToString(), JsonSerializer.Serialize(page), StringComparison.Ordinal);
        using (scopes.PushSystem())
            if (discover || use)
            {
                var policy = await policies.GetAsync(profile.Value.Uid, default);
                await policies.DeleteAsync(profile.Value.Uid, policy!.ETag, default);
            }
        var revoked = (await client.GetFromJsonAsync<SystemGraphPage>("/api/system-graph?namespace=graph-permissions"))!;
        Assert.IsEmpty(revoked.Nodes);
        using (scopes.PushSystem())
            Assert.AreEqual(profile.ETag, (await store.GetByUidAsync<ModelProfileResource>(profile.Value.Uid, default))!.ETag);
    }

    [TestMethod]
    public async Task SiblingAndCrossTenantConsumersNeverEnterWorkspaceProjection()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var now = DateTimeOffset.UnixEpoch;
        var sibling = new Workspace(Guid.NewGuid(), context.TenantId, "graph-sibling", "Sibling", WorkspaceStatus.Active, now);
        var tenant = new Tenant(Guid.NewGuid(), "graph-foreign", "Foreign", TenantStatus.Active, now);
        var foreign = new Workspace(Guid.NewGuid(), tenant.Id, "graph-foreign-workspace", "Foreign", WorkspaceStatus.Active, now);
        await identities.AddWorkspaceAsync(sibling, default);
        await identities.AddTenantAsync(tenant, default);
        await identities.AddWorkspaceAsync(foreign, default);
        var ns = new ResourceNamespace("graph-isolation");
        StoredResource<ModelProfileResource> shared;
        Guid[] hiddenConsumers;
        var workspaceOwner = ResourceScopeRef.Workspace(context.WorkspaceId);
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        using (scopes.PushSystem())
        {
            await store.PutExactAsync(ResourceScopeRef.Workspace(sibling.Id), Profile("invisible-sibling", ResourceScopeRef.Workspace(sibling.Id), ns), null, true, default);
            await store.PutExactAsync(ResourceScopeRef.Workspace(foreign.Id), Profile("invisible-foreign", ResourceScopeRef.Workspace(foreign.Id), ns), null, true, default);
            shared = await store.PutExactAsync(ResourceScopeRef.Instance, Profile("shared", ResourceScopeRef.Instance, ns), null, true, default);
            await policies.PutAsync(new(new(shared.Value.Uid, ResourceScopeRef.Instance, ns, shared.Value.Kind),
                [new(workspaceOwner, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
            await PutConsumerAsync(workspaceOwner, "visible-consumer");
            var siblingConsumer = await PutConsumerAsync(ResourceScopeRef.Workspace(sibling.Id), "invisible-sibling-consumer");
            var foreignConsumer = await PutConsumerAsync(ResourceScopeRef.Workspace(foreign.Id), "invisible-foreign-consumer");
            var ancestorScope = ResourceScopeRef.Tenant(context.TenantId);
            var ancestorConsumer = await PutConsumerAsync(ancestorScope, "invisible-ancestor-consumer");
            var ancestorRevision = await store.CreateImmutableAsync(new AgentRevision
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = AgentResourceKinds.AgentRevision,
                ScopeRef = ancestorScope,
                Metadata = new() { Name = "invisible-ancestor-revision", Namespace = ns },
                Generation = 1,
                AgentUid = ancestorConsumer.Value.Uid,
                AgentName = ancestorConsumer.Value.Name,
                AgentNamespace = ns,
                AgentVersion = 1,
                DefinitionHash = "private-hash",
                CreatedAt = now,
                ProvisioningState = ProvisioningState.Succeeded,
                Definition = new()
                {
                    AgentId = ancestorConsumer.Value.Uid,
                    AgentKey = ancestorConsumer.Value.Name,
                    AgentVersion = 1,
                    DisplayName = "Private",
                    Description = "",
                    EffectiveInstructions = "private",
                    ModelProfileName = "shared",
                    ModelProfileNamespace = ns,
                    ModelProfilePin = new(shared.Value.Uid, ResourceScopeRef.Instance, ns, shared.Value.Kind, shared.Value.Name, 1),
                    RuntimeProfileName = "maf-builtin",
                    EffectiveToolNames = [],
                    MiddlewareIds = [],
                    ContextProviderIds = [],
                    Capabilities = [],
                    Handler = "prompt-agent",
                    DefinitionHash = "private-hash"
                }
            }, default);
            hiddenConsumers = [siblingConsumer.Value.Uid, foreignConsumer.Value.Uid, ancestorConsumer.Value.Uid, ancestorRevision.Value.Uid];
        }
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Workspace(context.WorkspaceId));
        var response = await client.GetAsync("/api/system-graph?namespace=graph-isolation");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("invisible", text, StringComparison.Ordinal);
        Assert.DoesNotContain(sibling.Id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(foreign.Id.ToString(), text, StringComparison.Ordinal);
        var root = Uri.EscapeDataString(SystemGraphIds.Definition(shared.Value.Uid));
        var reverse = (await client.GetFromJsonAsync<SystemGraphPage>($"/api/system-graph?namespace=graph-isolation&root={root}"))!;
        Assert.HasCount(2, reverse.Nodes);
        Assert.HasCount(1, reverse.Relations);
        Assert.IsTrue(reverse.Nodes.Any(node => node.Identity.Name == "visible-consumer"));
        Assert.IsFalse(reverse.HasMore);
        foreach (var uid in hiddenConsumers)
            Assert.DoesNotContain(uid.ToString(), JsonSerializer.Serialize(reverse), StringComparison.Ordinal);

        Task<StoredResource<AgentResource>> PutConsumerAsync(ResourceScopeRef scope, string name) => store.PutExactAsync(scope, new AgentResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.Agent,
            ScopeRef = scope,
            Metadata = new() { Name = name, Namespace = ns },
            Generation = 1,
            Definition = new() { DisplayName = name, Instructions = "deterministic", ModelProfile = new("shared", ResourceScopeRef.Instance, ns) }
        }, null, true, default);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApiUsesCurrentResourceReadRbacAndRejectsRevokedRoles(bool canRead)
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "SystemGraphTest";
                options.DefaultChallengeScheme = "SystemGraphTest";
            }).AddScheme<AuthenticationSchemeOptions, GraphTestAuthenticationHandler>("SystemGraphTest", _ => { })));
        var bootstrap = await GetBootstrapContextAsync(factory);
        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        var now = DateTimeOffset.UnixEpoch;
        var principal = new Principal(Guid.NewGuid(), PrincipalKind.Human, "Graph reader", null, PrincipalStatus.Active, now);
        var role = new RoleDefinition(Guid.NewGuid(), "graph-reader", "Graph reader",
            canRead ? [AuthorizationPermissions.WorkspacesRead, AuthorizationPermissions.ResourcesRead] : [AuthorizationPermissions.WorkspacesRead], false);
        var assignment = new RoleAssignment(Guid.NewGuid(), bootstrap.TenantId, principal.Id, PrincipalType.User, role.Id, AuthorizationScopes.Workspace(bootstrap.WorkspaceId));
        await identities.AddPrincipalAsync(principal, default);
        await identities.AddExternalIdentityAsync(new(Guid.NewGuid(), "system-graph-test", principal.Id.ToString(), principal.Id, now), default);
        await identities.AddMembershipAsync(new(Guid.NewGuid(), bootstrap.TenantId, principal.Id, MembershipStatus.Active, now), default);
        await identities.AddWorkspaceMembershipAsync(new(Guid.NewGuid(), bootstrap.WorkspaceId, principal.Id, MembershipStatus.Active, now), default);
        await identities.AddRoleDefinitionAsync(role, default);
        await identities.AddRoleAssignmentAsync(assignment, default);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-SystemGraph-Test-Subject", principal.Id.ToString());
        client.DefaultRequestHeaders.Add("X-Agentstration-Workspace", bootstrap.WorkspaceId.ToString());
        var response = await client.GetAsync("/api/system-graph");
        Assert.AreEqual(canRead ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode, await response.Content.ReadAsStringAsync());
        foreach (var route in new[] { "/api/system-graph/impact?root=missing", "/api/system-graph/changes?from=missing&to=missing", "/api/system-graph/coverage" })
            Assert.AreEqual(canRead ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync(route)).StatusCode, route);
        await identities.RemoveRoleAssignmentAsync(assignment.Id, default);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/system-graph")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/system-graph/impact?root=missing")).StatusCode);
    }

    public sealed class GraphTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["X-SystemGraph-Test-Subject"].FirstOrDefault();
            if (subject is null) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new("iss", "system-graph-test"), new("sub", subject)], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new(principal, Scheme.Name)));
        }
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
