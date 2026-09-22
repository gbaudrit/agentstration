using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ParameterApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task CrudUsesExactScopesConcurrencyAndImmediateGrantRevocation()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ParameterResourceKinds.Parameter}"))!;
        var tenant = targets.Single(value => value.Kind == ResourceScopeKind.Tenant).ScopeRef;
        var workspace = targets.Single(value => value.Kind == ResourceScopeKind.Workspace).ScopeRef;

        using var tenantCreate = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest(
            "shared-setting", Properties("Tenant value"), tenant));
        Assert.AreEqual(HttpStatusCode.Created, tenantCreate.StatusCode);
        using var workspaceCreate = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest(
            "shared-setting", Properties("Workspace value"), workspace));
        Assert.AreEqual(HttpStatusCode.Created, workspaceCreate.StatusCode);

        using var tenantRead = await client.GetAsync(Url("shared-setting", tenant));
        using var workspaceRead = await client.GetAsync(Url("shared-setting", workspace));
        Assert.AreEqual("Tenant value", (await tenantRead.Content.ReadFromJsonAsync<ParameterResource>())!.Definition.Value.GetString());
        Assert.AreEqual("Workspace value", (await workspaceRead.Content.ReadFromJsonAsync<ParameterResource>())!.Definition.Value.GetString());

        var resolver = factory.Services.GetRequiredService<IParameterResolver>();
        var context = new ParameterResolutionContext(workspace,
            ResourceAddress.Create(ResourceNamespace.Default, "ModelProvider", "consumer"));
        var tenantContext = context with { ConsumerScopeRef = tenant };
        var reference = new ParameterReference(
            ResourceAddress.Create(ResourceNamespace.Default, ParameterResourceKinds.Parameter, "shared-setting"), tenant);
        var workspaceReference = reference with { ScopeRef = workspace };
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            Assert.AreEqual("Tenant value", (await resolver.ResolveAsync(reference, tenantContext))!.Value.GetString());
            await Assert.ThrowsExactlyAsync<ParameterAccessDeniedException>(() => resolver.ResolveAsync(reference, context));
            await Assert.ThrowsExactlyAsync<ParameterAccessDeniedException>(() => resolver.ResolveAsync(workspaceReference, tenantContext));
        }

        using var grant = await PutAsync(client, "shared-setting", tenant, tenantRead.Headers.ETag!.ToString(),
            Properties("Granted value", new() { Grants = [new(workspace)] }));
        Assert.AreEqual(HttpStatusCode.OK, grant.StatusCode);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            Assert.AreEqual("Granted value", (await resolver.ResolveAsync(reference, context))!.Value.GetString());

        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var siblingId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var foreignWorkspaceId = Guid.NewGuid();
        await identities.AddWorkspaceAsync(new Workspace(siblingId, tenant.TargetId!.Value,
            "sibling", "Sibling", WorkspaceStatus.Active, now), default);
        await identities.AddTenantAsync(new Tenant(otherTenantId, "other-tenant", "Other tenant", TenantStatus.Active, now), default);
        await identities.AddWorkspaceAsync(new Workspace(foreignWorkspaceId, otherTenantId,
            "foreign", "Foreign", WorkspaceStatus.Active, now), default);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            foreach (var deniedScope in new[] { ResourceScopeRef.Workspace(siblingId), ResourceScopeRef.Workspace(foreignWorkspaceId) })
            {
                var denied = context with { ConsumerScopeRef = deniedScope };
                await Assert.ThrowsExactlyAsync<ParameterAccessDeniedException>(() => resolver.ResolveAsync(reference, denied));
            }
        }
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity,
            (await client.GetAsync(Url("shared-setting", ResourceScopeRef.Workspace(foreignWorkspaceId)))).StatusCode);

        using var stale = await PutAsync(client, "shared-setting", tenant, tenantRead.Headers.ETag!.ToString(),
            Properties("Stale value", new() { Grants = [new(workspace)] }));
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);

        using var revoke = await PutAsync(client, "shared-setting", tenant, grant.Headers.ETag!.ToString(),
            Properties("Revoked value"));
        Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            await Assert.ThrowsExactlyAsync<ParameterAccessDeniedException>(() => resolver.ResolveAsync(reference, context));

        using var delete = new HttpRequestMessage(HttpMethod.Delete, Url("shared-setting", tenant));
        delete.Headers.IfMatch.ParseAdd(revoke.Headers.ETag!.ToString());
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(Url("shared-setting", tenant))).StatusCode);
    }

    [TestMethod]
    public async Task InvalidValuesAndGrantsHaveControlledOutcomes()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ParameterResourceKinds.Parameter}"))!;
        var tenant = targets.Single(value => value.Kind == ResourceScopeKind.Tenant).ScopeRef;

        using var wrongType = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest(
            "wrong-type", new()
            {
                DisplayName = "Wrong type",
                ValueType = ParameterValueType.WholeNumber,
                Value = JsonSerializer.SerializeToElement("not an integer")
            }, tenant));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, wrongType.StatusCode);

        using var oversized = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest(
            "oversized", Properties(new string('x', ParameterManagementService.MaximumValueBytes + 1)), tenant));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, oversized.StatusCode);

        using var invalidGrant = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest(
            "invalid-grant", Properties("value", new() { Grants = [new(ResourceScopeRef.Instance)] }), tenant));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidGrant.StatusCode);
    }

    [TestMethod]
    public async Task InUseParameterCannotBeDeletedAndUsageIsReported()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IParameterUsageProvider, TestUsageProvider>()));
        using var client = factory.CreateClient();
        var scope = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ParameterResourceKinds.Parameter}"))!
            .Single(value => value.Kind == ResourceScopeKind.Workspace).ScopeRef;
        using var created = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest(
            "in-use", Properties("value"), scope));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        var usages = await client.GetFromJsonAsync<ParameterUsagesResponse>(
            $"/api/parameters/in-use/usages?scopeRef={Uri.EscapeDataString(scope.Value)}");
        Assert.AreEqual(1, usages?.Count);
        Assert.AreEqual("ModelProvider", usages?.Value.Single().ResourceType);

        using var request = new HttpRequestMessage(HttpMethod.Delete, Url("in-use", scope));
        request.Headers.IfMatch.ParseAdd(created.Headers.ETag!.ToString());
        using var deleted = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode);
    }

    private static ParameterProperties Properties(string value, Agentstration.ResourceManagement.DescendantUsePolicy? policy = null) => new()
    {
        DisplayName = "Shared setting",
        Description = "Visible nonsecret configuration",
        ValueType = ParameterValueType.Text,
        Value = JsonSerializer.SerializeToElement(value),
        UsePolicy = policy ?? new()
    };

    private static string Url(string name, ResourceScopeRef scope) =>
        $"/api/parameters/{name}?scopeRef={Uri.EscapeDataString(scope.Value)}";

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string name, ResourceScopeRef scope,
        string etag, ParameterProperties properties)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Url(name, scope))
        {
            Content = JsonContent.Create(new PutParameterRequest(properties))
        };
        request.Headers.IfMatch.ParseAdd(etag);
        return await client.SendAsync(request);
    }

    private sealed class TestUsageProvider : IParameterUsageProvider
    {
        public Task<IReadOnlyList<ParameterUsage>> GetUsagesAsync(ScopedResourceAddress parameter,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ParameterUsage>>(
                [new("ModelProvider", "consumer", "Consumer", "/modelproviders/consumer")]);
    }
}
