using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ResourcePlanExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task PlanInspectionRejectsAnotherExecutionWorkspaceOrInventedNamespace()
    {
        await using var factory = Factory();
        var bootstrap = await GetBootstrapContextAsync(factory);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Workspace(bootstrap.WorkspaceId));
        var json = await client.GetFromJsonAsync<JsonElement>("/api/identity/context");
        var context = json.GetProperty("context");
        var workspace = context.GetProperty("workspaceId").GetGuid();
        var owner = ResourceScopeRef.Workspace(workspace);
        var stored = await factory.Services.GetRequiredService<IResourcePlanRepository>().CreateAsync(new(new(Guid.NewGuid()),
            new(context.GetProperty("tenantId").GetGuid(), new(workspace)), "Saved plan", "Saved goal", null,
            new(ResourcePlanningContractVersions.V1, JsonSerializer.SerializeToElement(new { })), ResourcePlanStatus.Ready, 2, new(Guid.Empty), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), default);
        string Path(ResourceScopeRef scope, string ns) => $"/api/resource-plans/{stored.Value.Id.Value:D}?scopeRef={Uri.EscapeDataString(scope.Value)}&namespace={ns}";
        using var response = await client.GetAsync(Path(owner, "default"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var plan = await response.Content.ReadFromJsonAsync<ResourcePlan>();
        Assert.IsNotNull(plan);
        Assert.AreEqual(stored.Value.Id, plan.Id);
        Assert.AreEqual(stored.Value.Scope, plan.Scope);
        Assert.AreEqual(stored.ETag, response.Headers.ETag?.ToString());
        using var otherOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid()), "default"));
        Assert.AreEqual(HttpStatusCode.NotFound, otherOwner.StatusCode);
        using var otherNamespace = await client.GetAsync(Path(owner, "invented"));
        Assert.AreEqual(HttpStatusCode.NotFound, otherNamespace.StatusCode);
    }
}
