using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class RuntimeAgentAccessValidatorTests
{
    [TestMethod]
    public void PhysicalWorkspaceCanCarryInstanceLogicalAccessScope()
    {
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var scope = RuntimeRunScope.Workspace(Guid.NewGuid(), workspaceId, Guid.NewGuid()) with
        {
            LogicalScopeRef = ResourceScopeRef.Instance
        };

        Assert.AreEqual(ResourceScopeRef.Workspace(workspaceId.Value), scope.ScopeRef);
        Assert.AreEqual(ResourceScopeRef.Instance, scope.AccessScopeRef);
    }

    [TestMethod]
    public void PhysicalWorkspaceCanCarryTenantLogicalAccessScope()
    {
        var tenantId = Guid.NewGuid();
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var scope = RuntimeRunScope.Workspace(tenantId, workspaceId, Guid.NewGuid()) with
        {
            LogicalScopeRef = ResourceScopeRef.Tenant(tenantId)
        };

        Assert.AreEqual(ResourceScopeRef.Workspace(workspaceId.Value), scope.ScopeRef);
        Assert.AreEqual(ResourceScopeRef.Tenant(tenantId), scope.AccessScopeRef);
    }

    [TestMethod]
    public void BusinessWorkspaceDefaultsToItsPhysicalAccessScope()
    {
        var tenantId = Guid.NewGuid();
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var scope = RuntimeRunScope.Workspace(tenantId, workspaceId, Guid.NewGuid());

        Assert.AreEqual(scope.ScopeRef, scope.AccessScopeRef);
    }
}
