using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ResourceScopeFormFieldTests
{
    [TestMethod]
    public void CompactBadgeKeepsScopeAsInlineMetadata()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<ResourceScopeBadge>(parameters => parameters
            .Add(component => component.ScopeRef, scope)
            .Add(component => component.Compact, true));

        var badge = rendered.Find(".resource-scope-badge-compact");
        Assert.AreEqual(scope.Value, badge.GetAttribute("title"));
        Assert.AreEqual("Workspace", badge.TextContent);
    }

    [TestMethod]
    public void SelectsTheWritableWorkspaceByDefault()
    {
        var tenant = new ResourceScopeTargetResponse(ResourceScopeRef.Tenant(Guid.NewGuid()), ResourceScopeKind.Tenant, "Development", true);
        var workspace = new ResourceScopeTargetResponse(ResourceScopeRef.Workspace(Guid.NewGuid()), ResourceScopeKind.Workspace, "Default", true);
        ResourceScopeRef? selected = null;
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeClient([tenant, workspace]));

        var rendered = context.Render<ResourceScopeFormField>(parameters => parameters
            .Add(component => component.ResourceKind, "Example")
            .Add(component => component.ValueChanged, value => selected = value));

        rendered.WaitForAssertion(() => Assert.AreEqual(workspace.ScopeRef, selected));
        Assert.AreEqual(workspace.ScopeRef.Value, rendered.Find("select").GetAttribute("value"));
    }

    private sealed class FakeClient(IReadOnlyList<ResourceScopeTargetResponse> targets) : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) => Task.FromResult(targets);
    }
}
