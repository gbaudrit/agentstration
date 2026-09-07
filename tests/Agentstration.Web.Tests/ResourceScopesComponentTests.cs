using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ResourceScopesComponentTests
{
    [TestMethod]
    public void CurrentWorkspaceResourcesLinkToTheirCanonicalConsolePages()
    {
        var tenantId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var workspaceScope = ResourceScopeRef.Workspace(workspaceId);
        var resources = new ResourceScopeInventoryItemResponse[]
        {
            new(Guid.NewGuid(), ResourceNamespace.Parse("team-a"), ResourceKinds.Agent, "support agent", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), ResourceNamespace.Default, ResourceKinds.Secret, "api key", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), ResourceNamespace.Default, ResourceKinds.AgentRevision, "support-agent--000001", DateTimeOffset.UtcNow)
        };
        var inventory = new ResourceScopeInventoryResponse(
        [
            new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, string.Empty, null, false, []),
            new(ResourceScopeRef.Tenant(tenantId), ResourceScopeKind.Tenant, "Development", ResourceScopeRef.Instance, false, []),
            new(workspaceScope, ResourceScopeKind.Workspace, "Default", ResourceScopeRef.Tenant(tenantId), true, resources)
        ]);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeClient(inventory));

        var rendered = context.Render<ResourceScopes>();

        Assert.IsNotNull(rendered.Find("a[href='/namespaces/team-a/agents/support%20agent']"));
        Assert.IsNotNull(rendered.Find($"a[href='/secrets/api%20key?scopeRef={Uri.EscapeDataString(workspaceScope.Value)}']"));
        Assert.HasCount(2, rendered.FindAll("a.resource-name-link"));
        Assert.HasCount(2, rendered.FindAll("td.resource-action a.text-button"));
        Assert.HasCount(1, rendered.FindAll(".scope-no-link"));
        Assert.HasCount(3, rendered.FindAll(".kind-summary-item"));
        Assert.IsEmpty(rendered.FindAll(".resource-kind-mark"));
    }

    private sealed class FakeClient(ResourceScopeInventoryResponse inventory) : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => Task.FromResult(inventory);
    }
}
