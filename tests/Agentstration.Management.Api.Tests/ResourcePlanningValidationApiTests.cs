using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourcePlanning;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ResourcePlanningValidationApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task MissingExplicitProfilesBlockMaterializationAndChangeSetCreation()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = new ResourcePlanScope(context.TenantId, new WorkspaceId(context.WorkspaceId));
        var plans = factory.Services.GetRequiredService<ResourcePlanService>();
        var changeSets = factory.Services.GetRequiredService<ResourceChangeSetService>();
        var content = FunctionalResourcePlanSerializer.Serialize(new()
        {
            Solution = new("Support", ["Resolve requests"]),
            Roles = [new("resolution", "Resolution", "Prepare a response", ["Answer requests"], ["Text generation"])]
        });
        var created = await plans.CreateAsync(scope, new("Support", "Resolve requests", null, content), context.PrincipalId, default);
        var ready = await plans.ChangeStatusAsync(scope, created.Value.Id, new(ResourcePlanStatus.Ready), created.ETag, context.PrincipalId, default);
        using var client = factory.CreateClient();
        using var emptyPreview = await client.PostAsync($"/api/resource-plans/{ready.Value.Id.Value}/materializations", null);
        Assert.AreEqual(HttpStatusCode.OK, emptyPreview.StatusCode);
        var missingSelection = await emptyPreview.Content.ReadFromJsonAsync<ResourcePlanMaterialization>();
        Assert.IsNotNull(missingSelection);
        Assert.IsTrue(missingSelection.Diagnostics.Any(value => value.Code == "planning_binding_required"));
        var bindings = new ResourcePlanMaterializationRequest([new("resolution", new("missing-model"), new("missing-runtime"))]);
        using var preview = await client.PostAsJsonAsync($"/api/resource-plans/{ready.Value.Id.Value}/materializations", bindings);
        Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
        var materialization = await preview.Content.ReadFromJsonAsync<ResourcePlanMaterialization>();
        Assert.IsNotNull(materialization);
        Assert.IsFalse(materialization.CanCreateChangeSet);
        Assert.AreEqual(2, materialization.Diagnostics.Count(value => value.Code == "planning_binding_not_found"));

        using var response = await client.PostAsJsonAsync($"/api/resource-plans/{ready.Value.Id.Value}/change-sets", bindings);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.HasCount(0, (await changeSets.ListAsync(scope, ready.Value.Id, 0, 10, default)).Items);
    }

    [TestMethod]
    public async Task ProfileSelectionsAreSavedAndReopenedThroughWorkspaceApi()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = new ResourcePlanScope(context.TenantId, new WorkspaceId(context.WorkspaceId));
        var plans = factory.Services.GetRequiredService<ResourcePlanService>();
        var content = FunctionalResourcePlanSerializer.Serialize(new()
        {
            Solution = new("Support", ["Resolve requests"]),
            Roles = [new("resolution", "Resolution", "Prepare a response", ["Answer requests"], ["Text generation"])]
        });
        var plan = await plans.CreateAsync(scope, new("Support", "Resolve requests", null, content), context.PrincipalId, default);
        using var client = factory.CreateClient();
        var path = $"/api/resource-plans/{plan.Value.Id.Value}/bindings";
        using var missing = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        var selection = new SaveResourcePlanBindingsRequest(plan.Value.Revision,
            [new("resolution", new("model-a"), null)]);
        using var saved = await client.PutAsJsonAsync(path, selection);
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        var etag = saved.Headers.ETag?.ToString();
        Assert.IsNotNull(etag);
        using var reopened = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, reopened.StatusCode);
        Assert.AreEqual(etag, reopened.Headers.ETag?.ToString());
        Assert.AreEqual("model-a", (await reopened.Content.ReadFromJsonAsync<ResourcePlanBindingDraft>())!.Bindings.Single().ModelProfile!.Name);
        using var conflict = await client.PutAsJsonAsync(path, selection);
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
        using var update = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(selection with
            {
                Bindings = [new("resolution", new("model-a"), new("runtime-a"))]
            })
        };
        update.Headers.TryAddWithoutValidation("If-Match", etag);
        using var updated = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
    }
}
