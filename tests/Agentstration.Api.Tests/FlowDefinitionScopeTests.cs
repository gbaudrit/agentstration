using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Resources;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task ExactScopeOperationsKeepSameFlowIdentityIsolated()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await using var fixture = await FlowFixture.CreateAsync(scopeHierarchy: new TestFlowScopeHierarchy(tenantId));
        var scopes = new[]
        {
            ResourceScopeRef.Instance,
            ResourceScopeRef.Tenant(tenantId),
            ResourceScopeRef.Workspace(TestScope.WorkspaceId.Value)
        };
        var flowId = new FlowId("shared-flow");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"));

        foreach (var scope in scopes)
        {
            var created = await fixture.Service.CreateExactAsync(
                scope,
                new CreateFlowCommand(flowId.Value, $"Owned by {scope.Value}.", "1.0.0", true, definition),
                ResourceNamespace.Default,
                default);

            Assert.AreEqual(scope, created.Value.ScopeRef);
            var exact = await fixture.Service.GetExactAsync(scope, flowId, default);
            Assert.IsNotNull(exact);
            Assert.AreEqual(scope, exact.Value.ScopeRef);

            var page = await fixture.Service.ListExactAsync(scope, ResourceNamespace.Default, 0, 10, default);
            Assert.HasCount(1, page.Items);
            Assert.AreEqual(scope, page.Items[0].Value.ScopeRef);

            var published = await fixture.Service.PublishVersionExactAsync(scope, flowId, "1.0.0", activate: true, default);
            Assert.AreEqual(scope, published.Value.ScopeRef);
            Assert.AreEqual(scope, (await fixture.Service.GetVersionExactAsync(scope, flowId, "1.0.0", default))!.Value.ScopeRef);
            var resolved = await fixture.Service.ResolveAsync(
                TestScope.WorkspaceId,
                new FlowReference(flowId, "1.0.0", UseActiveVersion: false, ScopeRef: scope),
                default);
            Assert.AreEqual(scope, resolved.ScopeRef);
        }

        var tenantFlow = await fixture.Service.GetExactAsync(ResourceScopeRef.Tenant(tenantId), flowId, default);
        Assert.IsNotNull(tenantFlow);
        await fixture.Service.DeleteExactAsync(ResourceScopeRef.Tenant(tenantId), flowId, tenantFlow.ETag, allowSystemManaged: false, default);

        Assert.IsNull(await fixture.Service.GetExactAsync(ResourceScopeRef.Tenant(tenantId), flowId, default));
        Assert.IsNotNull(await fixture.Service.GetExactAsync(ResourceScopeRef.Instance, flowId, default));
        Assert.IsNotNull(await fixture.Service.GetExactAsync(ResourceScopeRef.Workspace(TestScope.WorkspaceId.Value), flowId, default));
    }

    [TestMethod]
    public async Task UnqualifiedReferenceRejectsMultipleVisibleOwners()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await using var fixture = await FlowFixture.CreateAsync(scopeHierarchy: new TestFlowScopeHierarchy(tenantId));
        var flowId = new FlowId("ambiguous-flow");
        var command = new CreateFlowCommand(
            flowId.Value,
            null,
            "1.0.0",
            true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant")));

        foreach (var scope in new[] { ResourceScopeRef.Instance, ResourceScopeRef.Tenant(tenantId) })
        {
            await fixture.Service.CreateExactAsync(scope, command, ResourceNamespace.Default, default);
            await fixture.Service.PublishVersionExactAsync(scope, flowId, "1.0.0", activate: true, default);
        }

        await Assert.ThrowsExactlyAsync<FlowReferenceAmbiguousException>(() =>
            fixture.Service.ResolveAsync(TestScope.WorkspaceId, new FlowReference(flowId), default));
    }

    [TestMethod]
    public async Task ExactReferenceRejectsScopeOutsideConsumerHierarchy()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await using var fixture = await FlowFixture.CreateAsync(scopeHierarchy: new TestFlowScopeHierarchy(tenantId));
        var siblingScope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        await Assert.ThrowsExactlyAsync<FlowReferenceOutsideScopeException>(() =>
            fixture.Service.ResolveAsync(
                TestScope.WorkspaceId,
                new FlowReference(new FlowId("outside"), "1.0.0", UseActiveVersion: false, ScopeRef: siblingScope),
                default));
    }

    [TestMethod]
    public async Task InitializationMigratesLegacyWorkspaceDefinitionAndVersion()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var scopeRef = ResourceScopeRef.Workspace(TestScope.WorkspaceId.Value);
        var flowId = new FlowId("legacy-flow");
        var created = await fixture.Service.CreateExactAsync(
            scopeRef,
            new CreateFlowCommand(
                flowId.Value,
                null,
                "1.0.0",
                true,
                new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"))),
            ResourceNamespace.Default,
            default);
        var published = await fixture.Service.PublishVersionExactAsync(scopeRef, flowId, "1.0.0", activate: true, default);

        var newPrefix = $"scope:{scopeRef.Value}:flow:{ResourceNamespace.Default.Value}:{flowId.Value}";
        var oldPrefix = $"workspace:{TestScope.WorkspaceId}:flow:{ResourceNamespace.Default.Value}:{flowId.Value}";
        await using (var context = await fixture.ContextFactory.CreateDbContextAsync())
        {
            await RewriteAsLegacyAsync(context, newPrefix, oldPrefix, LegacyPayload(created.Value, TestScope.WorkspaceId));
            await RewriteAsLegacyAsync(context, $"{newPrefix}:version:1.0.0", $"{oldPrefix}:version:1.0.0", LegacyPayload(published.Value, TestScope.WorkspaceId));
        }

        await fixture.Service.InitializeAsync(default);

        var migrated = await fixture.Service.GetExactAsync(scopeRef, flowId, default);
        var migratedVersion = await fixture.Service.GetVersionExactAsync(scopeRef, flowId, "1.0.0", default);
        Assert.IsNotNull(migrated);
        Assert.IsNotNull(migratedVersion);
        Assert.AreEqual(scopeRef, migrated.Value.ScopeRef);
        Assert.AreEqual(scopeRef, migratedVersion.Value.ScopeRef);
    }

    private static async Task RewriteAsLegacyAsync(
        FlowDbContext context,
        string currentKey,
        string legacyKey,
        string legacyPayload) =>
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"FlowResources\" SET \"Key\" = {legacyKey}, \"Payload\" = {legacyPayload} WHERE \"Key\" = {currentKey}");

    private static string LegacyPayload<T>(T value, WorkspaceId workspaceId)
    {
        var root = JsonSerializer.SerializeToNode(value, JsonOptions)!.AsObject();
        root.Remove("scopeRef");
        root["workspaceId"] = JsonSerializer.SerializeToNode(workspaceId, JsonOptions);
        return root.ToJsonString(JsonOptions);
    }

    private sealed class TestFlowScopeHierarchy(Guid tenantId) : IFlowScopeHierarchy
    {
        public Task<IReadOnlyList<ResourceScopeRef>> ListVisibleAsync(
            ResourceScopeRef consumerScopeRef,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ResourceScopeRef> result = consumerScopeRef.Kind switch
            {
                ResourceScopeKind.Workspace => [consumerScopeRef, ResourceScopeRef.Tenant(tenantId), ResourceScopeRef.Instance],
                ResourceScopeKind.Tenant => [consumerScopeRef, ResourceScopeRef.Instance],
                _ => [consumerScopeRef]
            };
            return Task.FromResult(result);
        }
    }
}
