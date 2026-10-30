using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

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
            Guid.NewGuid(), "Alice", $"/workspaces/{workspaceId:D}", ConsoleContextKind.Workspace,
            tenantId, "acme", "ACME", workspaceId, "support", "Support",
            new HashSet<string>(["resources/read"], StringComparer.Ordinal),
            [new(workspaceId, tenantId, "acme", "ACME", "support", "Support")],
            [new($"/workspaces/{workspaceId:D}", ConsoleContextKind.Workspace, tenantId, "acme", "ACME", workspaceId, "support", "Support")]);
        var state = new ConsoleContextState(new StubProvider(snapshot));

        await state.LoadAsync(default);

        Assert.AreEqual("ACME", state.Current?.TenantDisplayName);
        Assert.AreEqual("Support", state.Current?.WorkspaceDisplayName);
        Assert.IsTrue(state.HasPermission("resources/read"));
        Assert.IsFalse(state.HasPermission("resources/write"));
        Assert.IsNull(state.Error);
    }

    [TestMethod]
    public async Task ConcurrentConsumersShareOneContextLoad()
    {
        var snapshot = new ConsoleContextSnapshot(
            Guid.NewGuid(), "Alice", Guid.NewGuid(), "acme", "ACME", Guid.NewGuid(), "support", "Support",
            new HashSet<string>(StringComparer.Ordinal), []);
        var provider = new DelayedProvider(snapshot);
        var state = new ConsoleContextState(provider);

        var first = state.LoadAsync(default);
        var second = state.LoadAsync(default);
        provider.Complete();
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, provider.CallCount);
        Assert.AreSame(snapshot, state.Current);
    }

    [TestMethod]
    public async Task NavigationContextFallsBackToTheLoadedSelectionBeforeUrlCanonicalization()
    {
        var snapshot = new ConsoleContextSnapshot(
            Guid.NewGuid(), "Alice", ResourceScopeRef.Instance.Value, ConsoleContextKind.Instance,
            Guid.NewGuid(), "system", "System", Guid.NewGuid(), "operations", "Operations",
            new HashSet<string>(StringComparer.Ordinal), [],
            [new(ResourceScopeRef.Instance.Value, ConsoleContextKind.Instance, null, null, null, null, null, "Instance")]);
        using var context = new BunitContext();
        context.Services.AddSingleton<IConsoleContextProvider>(new StubProvider(snapshot));
        context.Services.AddAgentstrationWebComponents();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelprofiles/default-reasoning?scopeRef=%2Finstance");
        await context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(default);

        var navigationContext = context.Services.GetRequiredService<IConsoleNavigationContext>();
        navigationContext.SetSelectedScopeRef(
            context.Services.GetRequiredService<ConsoleContextState>().Current?.SelectedScopeRef);

        Assert.AreEqual(ResourceScopeRef.Instance.Value, navigationContext.ScopeRef);
    }

    private sealed class StubProvider(ConsoleContextSnapshot snapshot) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class DelayedProvider(ConsoleContextSnapshot snapshot) : IConsoleContextProvider
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            await completion.Task.WaitAsync(cancellationToken);
            return snapshot;
        }

        public void Complete() => completion.SetResult();
    }
}
