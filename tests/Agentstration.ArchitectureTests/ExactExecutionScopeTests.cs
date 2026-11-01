using Agentstration.Resources;

namespace Agentstration.ArchitectureTests;

[TestClass]
public sealed class ExactExecutionScopeTests
{
    private static readonly Guid PrincipalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WorkspaceId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [TestMethod]
    public void FactoriesPreserveOnlyCoordinatesRequiredByTheExactScope()
    {
        var instance = ExactExecutionScope.Instance(PrincipalId);
        var tenant = ExactExecutionScope.Tenant(TenantId, PrincipalId);
        var workspace = ExactExecutionScope.Workspace(TenantId, WorkspaceId, PrincipalId);

        Assert.AreEqual(ResourceScopeRef.Instance, instance.ScopeRef);
        Assert.IsNull(instance.TenantId);
        Assert.IsNull(instance.WorkspaceId);
        Assert.AreEqual(ResourceScopeRef.Tenant(TenantId), tenant.ScopeRef);
        Assert.AreEqual(TenantId, tenant.TenantId);
        Assert.IsNull(tenant.WorkspaceId);
        Assert.AreEqual(ResourceScopeRef.Workspace(WorkspaceId), workspace.ScopeRef);
        Assert.AreEqual(TenantId, workspace.TenantId);
        Assert.AreEqual(new WorkspaceId(WorkspaceId), workspace.WorkspaceId);
    }

    [TestMethod]
    public void CreateRejectsCoordinatesThatDoNotMatchTheScope()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            ExactExecutionScope.Create(ResourceScopeRef.Instance, PrincipalId, TenantId));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ExactExecutionScope.Create(ResourceScopeRef.Tenant(TenantId), PrincipalId, null));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ExactExecutionScope.Create(ResourceScopeRef.Workspace(WorkspaceId), PrincipalId, TenantId, Guid.NewGuid()));
    }
}
