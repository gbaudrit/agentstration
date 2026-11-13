using Agentstration.Agents;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

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
            new(Guid.NewGuid(), ResourceNamespace.Parse("team-a"), AgentResourceKinds.Agent, "support agent", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), ResourceNamespace.Default, SecretResourceKinds.Secret, "api key", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), ResourceNamespace.Default, AgentResourceKinds.AgentRevision, "support-agent--000001", DateTimeOffset.UtcNow)
        };
        var inventory = new ResourceScopeInventoryResponse(
        [
            new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, string.Empty, null, false, []),
            new(ResourceScopeRef.Tenant(tenantId), ResourceScopeKind.Tenant, "Development", ResourceScopeRef.Instance, false, []),
            new(workspaceScope, ResourceScopeKind.Workspace, "Default", ResourceScopeRef.Tenant(tenantId), true, resources)
        ]);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeClient(inventory));
        var strings = context.Services.GetRequiredService<IStringLocalizer<ResourceScopesStrings>>();

        var rendered = context.Render<ResourceScopes>();

        Assert.IsNotNull(rendered.Find($"a[href='/namespaces/team-a/agents/support%20agent?scopeRef={Uri.EscapeDataString(workspaceScope.Value)}&context={Uri.EscapeDataString(workspaceScope.Value)}']"));
        Assert.IsNotNull(rendered.Find($"a[href='/secrets/api%20key?scopeRef={Uri.EscapeDataString(workspaceScope.Value)}&context={Uri.EscapeDataString(workspaceScope.Value)}']"));
        Assert.HasCount(2, rendered.FindAll("a.resource-name-link"));
        Assert.IsEmpty(rendered.FindAll("td.resource-action"));
        Assert.HasCount(4, rendered.FindAll(".data-grid thead th"));
        Assert.HasCount(3, rendered.FindAll(".collection-icon-badges .scope-letter"));
        Assert.IsEmpty(rendered.FindAll(".kind-summary"));
        Assert.HasCount(4, rendered.FindAll("[data-testid='resource-kind-filter'] option"));
        StringAssert.Contains(rendered.Find("[data-testid='resource-kind-filter']").TextContent, "Agent (1)");
        Assert.AreEqual(strings["ShowingResources", 3, 3].Value, rendered.Find("[data-testid='resource-result-count']").TextContent);

        rendered.Find("[data-testid='resource-kind-filter']").Change(SecretResourceKinds.Secret);

        Assert.HasCount(1, rendered.FindAll("tr.resource-row"));
        Assert.AreEqual(strings["ShowingResources", 1, 3].Value, rendered.Find("[data-testid='resource-result-count']").TextContent);
        Assert.IsEmpty(rendered.FindAll(".resource-kind-mark"));
    }

    [TestMethod]
    public void WorkspaceRootRendersWithoutInventingAncestors()
    {
        var workspaceScope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var inventory = new ResourceScopeInventoryResponse(
        [
            new(workspaceScope, ResourceScopeKind.Workspace, "Isolated workspace", null, true, [])
        ]);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeClient(inventory));

        var rendered = context.Render<ResourceScopes>();

        var node = rendered.Find(".scope-tree-node");
        Assert.HasCount(1, rendered.FindAll(".scope-tree-node"));
        Assert.AreEqual("true", node.GetAttribute("aria-current"));
        Assert.AreEqual("--scope-depth: 0", node.GetAttribute("style"));
        StringAssert.Contains(node.TextContent, "Isolated workspace");
        Assert.IsEmpty(rendered.FindAll(".scope-kind-instance, .scope-kind-tenant"));
        Assert.AreEqual(workspaceScope.Value, rendered.Find(".scope-reference").TextContent);
    }

    [TestMethod]
    public void TenantRootRendersItsWorkspaceChildrenAndSupportsSelection()
    {
        var tenantScope = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspaceScope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var inventory = new ResourceScopeInventoryResponse(
        [
            new(tenantScope, ResourceScopeKind.Tenant, "Visible tenant", null, true, []),
            new(workspaceScope, ResourceScopeKind.Workspace, "Visible workspace", tenantScope, false,
            [new(Guid.NewGuid(), ResourceNamespace.Default, AgentResourceKinds.AgentRevision, "workspace-revision", DateTimeOffset.UtcNow)])
        ]);
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeClient(inventory));

        var rendered = context.Render<ResourceScopes>();

        Assert.HasCount(2, rendered.FindAll(".scope-tree-node"));
        Assert.IsEmpty(rendered.FindAll(".scope-kind-instance"));
        Assert.AreEqual("--scope-depth: 0", rendered.Find(".scope-kind-tenant").GetAttribute("style"));
        Assert.AreEqual("--scope-depth: 1", rendered.Find(".scope-kind-workspace").GetAttribute("style"));
        rendered.Find(".scope-kind-workspace").Click();

        Assert.AreEqual("true", rendered.Find(".scope-kind-workspace").GetAttribute("aria-current"));
        Assert.AreEqual("Visible workspace", rendered.Find(".scope-detail h2").TextContent);
        Assert.AreEqual(workspaceScope.Value, rendered.Find(".scope-reference").TextContent);
        StringAssert.Contains(rendered.Find("tr.resource-row").TextContent, "workspace-revision");
        Assert.IsEmpty(rendered.FindAll("tr.resource-row a"));
    }

    private sealed class FakeClient(ResourceScopeInventoryResponse inventory) : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => Task.FromResult(inventory);
    }
}
