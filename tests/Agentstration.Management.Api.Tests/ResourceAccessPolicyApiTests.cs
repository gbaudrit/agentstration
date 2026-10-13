using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ResourceAccessPolicyApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task CrudAndEvaluationUseExactResourceIdentityAndIndependentConcurrency()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ParameterResourceKinds.Parameter}"))!;
        var tenant = targets.Single(value => value.Kind == ResourceScopeKind.Tenant).ScopeRef;
        var workspace = targets.Single(value => value.Kind == ResourceScopeKind.Workspace).ScopeRef;

        using var createdResponse = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest(
            "shared-policy-api", new ParameterProperties
            {
                DisplayName = "Shared policy API",
                ValueType = ParameterValueType.Text,
                Value = JsonSerializer.SerializeToElement("value")
            }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        var resourceEtag = createdResponse.Headers.ETag!.ToString();
        var resource = (await createdResponse.Content.ReadFromJsonAsync<ParameterResource>())!;
        var identity = new ResourceAccessIdentity(resource.Uid, tenant, resource.Namespace,
            resource.Kind, resource.Generation);
        var policy = new ResourceAccessPolicy(identity,
        [
            new(workspace, [ResourceAccessAction.Use])
        ]);

        using var put = await client.PutAsJsonAsync($"/api/resource-access-policies/{resource.Uid:D}", policy);
        Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);
        var policyEtag = put.Headers.ETag!.ToString();
        Assert.AreNotEqual(resourceEtag, policyEtag);

        using var read = await client.GetAsync($"/api/resource-access-policies/{resource.Uid:D}");
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual(policyEtag, read.Headers.ETag!.ToString());
        var saved = (await read.Content.ReadFromJsonAsync<ResourceAccessPolicy>())!;
        Assert.AreEqual(resource.Uid, saved.Resource.Uid);
        Assert.AreEqual(workspace, saved.Grants.Single().TargetScopeRef);

        var listed = await client.GetFromJsonAsync<ValueResponse<ResourceAccessPolicyResponse>>(
            $"/api/resource-access-policies?ownerScopeRef={Uri.EscapeDataString(tenant.Value)}");
        Assert.AreEqual(resource.Uid, listed!.Value.Single().Policy.Resource.Uid);

        using var allowed = await client.PostAsJsonAsync(
            $"/api/resource-access-policies/{resource.Uid:D}/evaluate",
            new EvaluateResourceAccessRequest(identity, workspace, ResourceAccessAction.Use));
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        Assert.AreEqual(new ResourceAccessDecision(true, ResourceAccessDecisionReasons.ExactGrant),
            await allowed.Content.ReadFromJsonAsync<ResourceAccessDecision>());

        using var denied = await client.PostAsJsonAsync(
            $"/api/resource-access-policies/{resource.Uid:D}/evaluate",
            new EvaluateResourceAccessRequest(identity, workspace, ResourceAccessAction.Discover));
        Assert.AreEqual(HttpStatusCode.OK, denied.StatusCode);
        var deniedDecision = await denied.Content.ReadFromJsonAsync<ResourceAccessDecision>();
        Assert.IsFalse(deniedDecision!.Allowed);
        Assert.AreEqual(ResourceAccessDecisionReasons.GrantMissing, deniedDecision.ReasonCode);

        using var staleDelete = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/resource-access-policies/{resource.Uid:D}");
        staleDelete.Headers.IfMatch.ParseAdd("\"stale\"");
        using var staleDeleteResponse = await client.SendAsync(staleDelete);
        Assert.AreEqual(HttpStatusCode.Conflict, staleDeleteResponse.StatusCode);

        using var resourceRead = await client.GetAsync(
            $"/api/parameters/shared-policy-api?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        Assert.AreEqual(resourceEtag, resourceRead.Headers.ETag!.ToString());

        using var delete = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/resource-access-policies/{resource.Uid:D}");
        delete.Headers.IfMatch.ParseAdd(policyEtag);
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/resource-access-policies/{resource.Uid:D}")).StatusCode);
    }
}
