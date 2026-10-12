using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task ExactScopeOperationsKeepSameFlowIdentityIsolated()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
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
}
