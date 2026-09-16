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
    public async Task MissingDefaultModelProfileBlocksValidationAndPersistsAgentIssue()
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
        var changeSet = await changeSets.CreateAsync(scope, ready.Value.Id, context.PrincipalId, default);

        using var client = factory.CreateClient();
        using var response = await client.PostAsync($"/api/resource-plans/change-sets/{changeSet.Value.Id.Value}/validations", null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var validation = await response.Content.ReadFromJsonAsync<ResourceChangeSetValidation>();
        Assert.IsNotNull(validation);
        Assert.AreEqual(ResourceChangeSetReadiness.Blocked, validation.Readiness);
        var issue = validation.Issues.Single(value => value.Code == "resource_change_model_profile_invalid");
        Assert.AreEqual("changes[0].proposed.definition.modelProfile", issue.Path);
        Assert.Contains("resolution", issue.Message, StringComparison.Ordinal);
        Assert.Contains("default/default", issue.Message, StringComparison.Ordinal);
        Assert.AreEqual(ResourceChangeSetStatus.Proposed, (await changeSets.GetAsync(scope, changeSet.Value.Id, default)).Value.Status);

        var recorded = await client.GetFromJsonAsync<IReadOnlyList<ResourceChangeSetValidation>>(
            $"/api/resource-plans/change-sets/{changeSet.Value.Id.Value}/validations");
        Assert.IsNotNull(recorded);
        Assert.HasCount(1, recorded);
        Assert.AreEqual(validation.Id, recorded[0].Id);
    }
}
