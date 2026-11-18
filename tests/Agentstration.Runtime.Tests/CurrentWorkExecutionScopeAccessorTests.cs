using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Resources;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class CurrentWorkExecutionScopeAccessorTests
{
    private static readonly RequestContext OperationalContext = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"));

    [TestMethod]
    public void ExposesOperationalWorkspaceForInstanceAdministration()
    {
        var context = new CurrentRequestContext();
        var accessor = new CurrentWorkExecutionScopeAccessor(context);

        using var scope = context.PushInstance(OperationalContext);

        AssertOperationalScope(accessor.Current);
        Assert.AreEqual(ResourceScopeRef.Instance, accessor.Current?.LogicalScopeRef);
        Assert.AreEqual(ResourceScopeRef.Instance, accessor.Current?.AccessScopeRef);
    }

    [TestMethod]
    public void ExposesOperationalWorkspaceForTenantAdministration()
    {
        var context = new CurrentRequestContext();
        var accessor = new CurrentWorkExecutionScopeAccessor(context);

        using var scope = context.PushTenantAdministration(OperationalContext);

        AssertOperationalScope(accessor.Current);
        Assert.AreEqual(ResourceScopeRef.Tenant(OperationalContext.TenantId), accessor.Current?.LogicalScopeRef);
        Assert.AreEqual(ResourceScopeRef.Tenant(OperationalContext.TenantId), accessor.Current?.AccessScopeRef);
    }

    [TestMethod]
    public void DoesNotInventWorkspaceForLogicalTenantScope()
    {
        var context = new CurrentRequestContext();
        var accessor = new CurrentWorkExecutionScopeAccessor(context);

        using var scope = context.PushTenant(OperationalContext.PrincipalId, OperationalContext.TenantId);

        Assert.IsNull(accessor.Current);
    }

    private static void AssertOperationalScope(Agentstration.Flows.FlowRunScope? actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual(OperationalContext.PrincipalId, actual.PrincipalId);
        Assert.AreEqual(OperationalContext.TenantId, actual.TenantId);
        Assert.AreEqual(OperationalContext.WorkspaceId, actual.WorkspaceId.Value);
    }
}
