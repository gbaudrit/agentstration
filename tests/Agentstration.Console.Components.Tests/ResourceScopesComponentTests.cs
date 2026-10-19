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
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeClient(inventory));
        var strings = context.Services.GetRequiredService<IStringLocalizer<ResourceScopesStrings>>();

        var rendered = context.Render<ResourceScopes>();

        Assert.IsNotNull(rendered.Find("a[href='/namespaces/team-a/agents/support%20agent']"));
        Assert.IsNotNull(rendered.Find($"a[href='/secrets/api%20key?scopeRef={Uri.EscapeDataString(workspaceScope.Value)}']"));
        Assert.HasCount(2, rendered.FindAll("a.resource-name-link"));
        Assert.IsEmpty(rendered.FindAll("td.resource-action"));
        Assert.HasCount(4, rendered.FindAll(".data-grid thead th"));
        Assert.IsEmpty(rendered.FindAll(".kind-summary"));
        Assert.HasCount(4, rendered.FindAll("[data-testid='resource-kind-filter'] option"));
        StringAssert.Contains(rendered.Find("[data-testid='resource-kind-filter']").TextContent, "Agent (1)");
        Assert.AreEqual(strings["ShowingResources", 3, 3].Value, rendered.Find("[data-testid='resource-result-count']").TextContent);

        rendered.Find("[data-testid='resource-kind-filter']").Change(SecretResourceKinds.Secret);

        Assert.HasCount(1, rendered.FindAll("tr.resource-row"));
        Assert.AreEqual(strings["ShowingResources", 1, 3].Value, rendered.Find("[data-testid='resource-result-count']").TextContent);
        Assert.IsEmpty(rendered.FindAll(".resource-kind-mark"));
    }

    private sealed class FakeClient(ResourceScopeInventoryResponse inventory) : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => Task.FromResult(inventory);
    }
}
