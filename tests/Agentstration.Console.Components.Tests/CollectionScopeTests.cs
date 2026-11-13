using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class CollectionScopeTests
{
    [TestMethod]
    public async Task LongNamesUseTheAuthorizedSnapshotAndDoNotFetchPerBadge()
    {
        var tenant = Guid.NewGuid();
        var scope = ResourceScopeRef.Tenant(tenant);
        var longName = $"Tenant {new string('a', 150)}";
        var provider = new ContextProvider(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Administrator", scope.Value, ConsoleContextKind.Tenant,
            tenant, "tenant", longName, Guid.Empty, string.Empty, string.Empty,
            new HashSet<string>(), [],
            [new(scope.Value, ConsoleContextKind.Tenant, tenant, "tenant", longName, null, null, longName)]));
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var state = new ConsoleContextState(provider);
        context.Services.AddSingleton(state);
        var first = context.Render<ResourceScopeBadge>(parameters => parameters.Add(value => value.ScopeRef, scope).Add(value => value.ShowName, true));
        Assert.IsEmpty(first.FindAll(".disclosure-trigger"));
        await state.LoadAsync(CancellationToken.None);
        first.WaitForAssertion(() => Assert.AreEqual(longName, first.Find(".disclosure-trigger").TextContent));
        Assert.AreEqual("Tenant", first.Find(".resource-scope-badge").TextContent);
        Assert.AreEqual(longName, first.Find("[role='tooltip']").TextContent);
        Assert.AreEqual(longName, first.Find(".disclosure-trigger").GetAttribute("title"));
        Assert.AreEqual("manual", first.Find("[role='tooltip']").GetAttribute("popover"));
        var second = context.Render<ResourceScopeBadge>(parameters => parameters.Add(value => value.ScopeRef, scope).Add(value => value.ShowName, true));
        Assert.AreEqual(longName, second.Find(".disclosure-trigger").TextContent);
        var unauthorized = context.Render<ResourceScopeBadge>(parameters => parameters.Add(value => value.ScopeRef, ResourceScopeRef.Tenant(Guid.NewGuid())).Add(value => value.ShowName, true));
        Assert.IsEmpty(unauthorized.FindAll(".disclosure-trigger"));
        var icon = context.Render<ResourceScopeBadge>(parameters => parameters.Add(value => value.ScopeRef, scope).Add(value => value.IconOnly, true));
        Assert.AreEqual($"Tenant — {longName}", icon.Find("button").GetAttribute("aria-label"));
        Assert.AreEqual($"Tenant — {longName}", icon.Find("[role=tooltip]").TextContent);
        Assert.AreEqual("T", icon.Find(".scope-letter").TextContent);
        Assert.AreEqual("true", icon.Find(".scope-letter").GetAttribute("aria-hidden"));
        var unknown = context.Render<ResourceScopeBadge>(parameters => parameters.Add(value => value.ScopeRef, ResourceScopeRef.Tenant(Guid.NewGuid())).Add(value => value.IconOnly, true));
        Assert.AreEqual("Tenant", unknown.Find("button").GetAttribute("aria-label"));
        Assert.AreEqual(1, provider.Calls);
    }

    private sealed class ContextProvider(ConsoleContextSnapshot snapshot) : IConsoleContextProvider
    {
        public int Calls { get; private set; }
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(snapshot);
        }
    }
}
