using Agentstration.Web.Components.State;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ConsoleContextStateTests
{
    [TestMethod]
    public async Task LoadsDynamicOrganizationWorkspaceAndPermissions()
    {
        var workspaceId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var snapshot = new ConsoleContextSnapshot(
            Guid.NewGuid(), "Alice", tenantId, "acme", "ACME", workspaceId, "support", "Support",
            new HashSet<string>(["resources/read"], StringComparer.Ordinal),
            [new(workspaceId, tenantId, "acme", "ACME", "support", "Support")]);
        var state = new ConsoleContextState(new StubProvider(snapshot));

        await state.LoadAsync(default);

        Assert.AreEqual("ACME", state.Current?.TenantDisplayName);
        Assert.AreEqual("Support", state.Current?.WorkspaceDisplayName);
        Assert.IsTrue(state.HasPermission("resources/read"));
        Assert.IsFalse(state.HasPermission("resources/write"));
        Assert.IsNull(state.Error);
    }

    private sealed class StubProvider(ConsoleContextSnapshot snapshot) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
