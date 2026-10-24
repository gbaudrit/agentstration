using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Resources;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class RuntimeAgentAccessValidatorTests
{
    [TestMethod]
    public void InstanceOperationsWorkspaceUsesInstanceAsItsLogicalAccessScope()
    {
        var executionScope = ResourceScopeRef.Workspace(
            OperationalTopologyIdentities.InstanceOperationsWorkspaceId);

        var accessScope = RuntimeAgentAccessValidator.MapOperationalScope(executionScope, null);

        Assert.AreEqual(ResourceScopeRef.Instance, accessScope);
    }

    [TestMethod]
    public void TenantOperationsWorkspaceUsesTenantAsItsLogicalAccessScope()
    {
        var tenantId = Guid.NewGuid();
        var workspace = new Workspace(
            OperationalTopologyIdentities.TenantOperationsWorkspaceId(tenantId),
            tenantId,
            OperationalTopologyIdentities.TenantOperationsWorkspaceName,
            OperationalTopologyIdentities.TenantOperationsWorkspaceDisplayName,
            WorkspaceStatus.Active,
            DateTimeOffset.UtcNow);
        var executionScope = ResourceScopeRef.Workspace(workspace.Id);

        var accessScope = RuntimeAgentAccessValidator.MapOperationalScope(executionScope, workspace);

        Assert.AreEqual(ResourceScopeRef.Tenant(tenantId), accessScope);
    }

    [TestMethod]
    public void BusinessWorkspaceRemainsItsOwnLogicalAccessScope()
    {
        var tenantId = Guid.NewGuid();
        var workspace = new Workspace(
            Guid.NewGuid(),
            tenantId,
            "business",
            "Business",
            WorkspaceStatus.Active,
            DateTimeOffset.UtcNow);
        var executionScope = ResourceScopeRef.Workspace(workspace.Id);

        var accessScope = RuntimeAgentAccessValidator.MapOperationalScope(executionScope, workspace);

        Assert.AreEqual(executionScope, accessScope);
    }
}
