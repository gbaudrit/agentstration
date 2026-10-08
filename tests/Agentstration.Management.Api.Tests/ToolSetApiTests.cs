using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ToolSetApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task PublishedToolSetRoutesDeterministicallyAndDetectsAmbiguousAndStaleMembers()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        await SeedToolAsync(store, scopeRef, "documents.search", "search");
        await SeedToolAsync(store, scopeRef, "documents.query", "query");
        var service = factory.Services.GetRequiredService<ToolSetService>();
        var missing = await Assert.ThrowsAsync<ToolSetValidationException>(() => service.CreateAsync(
            ToolSet(scopeRef, "missing-member", "1.0.0", [Member("missing", "knowledge.read", "read")]), default));
        Assert.AreEqual("tool_set_member_not_found", missing.Code);

        using var client = factory.CreateClient();
        var definition = ToolSet(scopeRef, "documents", "1.0.0",
        [
            Member("documents.search", "knowledge.retrieve", "search"),
            Member("documents.query", "knowledge.retrieve", "query")
        ]).Definition;
        using var createdResponse = await client.PostAsJsonAsync("/api/toolsets",
            new Agentstration.Tools.Contracts.CreateToolSetRequest("documents", definition));
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<ToolSetResource>();
        Assert.IsNotNull(created);
        var published = await service.GetVersionAsync(default, "documents", "1.0.0", default);

        Assert.IsNotNull(published);
        Assert.AreEqual(created.Uid, published.Value.ToolSetUid);
        var ambiguous = await Assert.ThrowsAsync<ToolSetValidationException>(() =>
            service.ResolveRouteAsync(default, "documents", "1.0.0", "knowledge.retrieve", null, default));
        Assert.AreEqual("tool_route_ambiguous", ambiguous.Code);
        var selected = await service.ResolveRouteAsync(default, "documents", "1.0.0", "knowledge.retrieve", "search", default);
        Assert.AreEqual("documents.search", selected.Member.ToolName);
        Assert.AreEqual("test-provider", selected.Member.ProviderName);

        var current = await store.GetAsync<ToolResource>(new(ToolResourceKinds.Tool, "documents.search"), default);
        Assert.IsNotNull(current);
        await store.PutAsync(current.Value with { Generation = 2 }, current.ETag, false, default);
        var stale = await Assert.ThrowsAsync<ToolSetValidationException>(() =>
            service.ResolveRouteAsync(default, "documents", "1.0.0", "knowledge.retrieve", "search", default));
        Assert.AreEqual("tool_set_member_stale", stale.Code);
        var actions = (await factory.Services.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(50, default))
            .Where(value => value.WorkspaceId == context.WorkspaceId)
            .Select(value => value.Action)
            .ToArray();
        CollectionAssert.IsSubsetOf(new[]
        {
            SecurityAuditActions.ToolSetCreated,
            SecurityAuditActions.ToolSetVersionPublished
        }, actions);
    }

    [TestMethod]
    public async Task AgentRevisionPinsSelectedToolSetMembersWithoutFuturePrivilegeExpansion()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        await SeedToolAsync(store, scopeRef, "docs.search", "search");
        await SeedToolAsync(store, scopeRef, "docs.read", "read");
        var sets = factory.Services.GetRequiredService<ToolSetService>();
        var v1 = await sets.CreateAsync(ToolSet(scopeRef, "docs", "1.0.0",
            [Member("docs.search", "knowledge.search", "search")]), default);
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        _ = await agents.PutAgentAsync(Agent("docs-agent", "1.0.0", ["docs.search"]), null, true, default);
        var spec = new AgentDeploymentSpec
        {
            Environment = "local",
            RuntimeProfileName = "maf-builtin",
            HostingMode = AgentHostingMode.InProcess
        };
        var revision = await agents.CreateRevisionAsync("docs-agent", spec, default);

        _ = await sets.PutAsync(default, "docs", v1.Value.Definition with
        {
            Version = "2.0.0",
            Members =
            [
                Member("docs.search", "knowledge.search", "search"),
                Member("docs.read", "knowledge.read", "read")
            ]
        }, v1.ETag, default);

        CollectionAssert.AreEqual(new[] { "docs.search" }, revision.Value.Definition.EffectiveToolNames.ToArray());
        Assert.HasCount(1, revision.Value.Definition.ToolSetAssignments);
        Assert.AreEqual("1.0.0", revision.Value.Definition.ToolSetAssignments.Single().Version);
        CollectionAssert.AreEqual(new[] { "docs.search" }, revision.Value.Definition.ToolSetAssignments.Single().Members.ToArray());
        var inUse = await Assert.ThrowsAsync<ToolSetValidationException>(() =>
            sets.DeleteAsync(default, "docs", null, default));
        Assert.AreEqual("tool_set_in_use_by_agent", inUse.Code);

        AgentResource Agent(string name, string version, IReadOnlyList<string> members) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = name },
            Definition = new AgentProperties
            {
                DisplayName = "Docs agent",
                Instructions = "Use only assigned documentation tools.",
                ModelProfile = new ResourceReference("reasoning-default"),
                ToolSets = [new() { ToolSet = new ResourceReference("docs"), Version = version, Members = members }]
            }
        };
    }

    [TestMethod]
    public async Task ToolSetCannotReferenceAnotherWorkspace()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var service = factory.Services.GetRequiredService<ToolSetService>();
        var foreignScope = ResourceScopeRef.Workspace(Guid.NewGuid());

        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            ToolSet(foreignScope, "foreign", "1.0.0", [Member("missing", "knowledge.read", "read")]), default));
        StringAssert.Contains(denied.Message, "was not found");
    }

    private static ToolSetResource ToolSet(ResourceScopeRef scopeRef, string name, string version, IReadOnlyList<ToolSetMember> members) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolSet,
        Metadata = new ResourceMetadata
        {
            Name = name,
            Tags = new Dictionary<string, string> { ["agentstration.io/category"] = "knowledge-source" }
        },
        ScopeRef = scopeRef,
        Definition = new ToolSetProperties
        {
            DisplayName = name,
            Category = new ResourceReference("knowledge-source"),
            Version = version,
            Publish = true,
            Members = members
        }
    };

    private static ToolSetMember Member(string name, string capability, string route) => new()
    {
        Tool = new ResourceReference(name),
        Capability = capability,
        Route = route
    };

    private static async Task SeedToolAsync(IResourceStore store, ResourceScopeRef scopeRef, string name, string externalId)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var provider = await store.GetAsync<ToolProviderResource>(new(ToolResourceKinds.ToolProvider, "test-provider"), default);
        if (provider is null)
            _ = await store.PutExactAsync(scopeRef, new ToolProviderResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolProvider,
                Metadata = new ResourceMetadata { Name = "test-provider" },
                ScopeRef = scopeRef,
                Generation = 1,
                Definition = new ToolProviderProperties
                {
                    DisplayName = "Test provider",
                    ProviderType = ToolProviderType.Mcp,
                    Mcp = new McpToolProviderConfiguration { Internal = true }
                }
            }, null, true, default);
        _ = await store.PutExactAsync(scopeRef, new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = name },
            ScopeRef = scopeRef,
            Generation = 1,
            Definition = new ToolResourceProperties
            {
                DisplayName = name,
                Provider = new ResourceReference("test-provider"),
                ExternalId = externalId,
                Discovery = new ToolDiscoveryState { Available = true, FirstSeenAt = now, LastSeenAt = now },
                Schema = new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }) }
            }
        }, null, true, default);
    }
}
