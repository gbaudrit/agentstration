using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class ConnectedServiceApiTests
{
    [TestMethod]
    public async Task NamespacedServiceCommandsPreserveHomonymousDefaultIdentityAndEnforceOwnerAndEtag()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var tenant = await TenantScopeAsync(client);
        var workspace = await WorkspaceScopeAsync(client);
        SelectConsoleContext(client, tenant);
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var descriptor = new ConnectedServiceTypeVersionDescriptor("1.0.0", AepSchemaDigest.Compute(schema), schema, [], []);
        using var typeResponse = await client.PostAsJsonAsync("/api/connected-service-types", new CreateConnectedServiceTypeRequest("namespace-fixture", new()
        { DisplayName = "Namespace fixture", PreferredVersion = "1.0.0", Versions = [descriptor] }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, typeResponse.StatusCode);
        var type = (await typeResponse.Content.ReadFromJsonAsync<ConnectedServiceTypeResource>())!;
        var properties = new ConnectedServiceProperties
        {
            DisplayName = "Default service",
            Type = new() { Type = new(type.Name, tenant, type.Namespace), TypeUid = type.Uid, Version = descriptor.Version, SchemaDigest = descriptor.SchemaDigest },
            Options = JsonSerializer.SerializeToElement(new { })
        };
        using var defaultResponse = await client.PostAsJsonAsync("/api/connected-services", new CreateConnectedServiceRequest("same-service", properties, tenant));
        Assert.AreEqual(HttpStatusCode.Created, defaultResponse.StatusCode);
        var defaultResource = (await defaultResponse.Content.ReadFromJsonAsync<ConnectedServiceResource>())!;
        ConnectedServiceResource namespaced;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            namespaced = (await factory.Services.GetRequiredService<ConnectedServiceManagementService>().CreateAsync(defaultResource with
            {
                Uid = Guid.NewGuid(),
                Metadata = defaultResource.Metadata with { Namespace = ResourceNamespace.Parse("fixture") },
                Definition = properties with { DisplayName = "Namespaced service" }
            }, default)).Value;
        }
        var prefix = $"/api/connected-services/same-service?scopeRef={Uri.EscapeDataString(tenant.Value)}&namespace=fixture";
        using var read = await client.GetAsync(prefix);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual(namespaced.Uid, (await read.Content.ReadFromJsonAsync<ConnectedServiceResource>())!.Uid);
        var etag = read.Headers.ETag!;
        using var usages = await client.GetAsync(prefix.Replace("?", "/usages?", StringComparison.Ordinal));
        Assert.AreEqual(HttpStatusCode.OK, usages.StatusCode);
        using var operations = await client.GetAsync(prefix.Replace("?", "/operations?", StringComparison.Ordinal));
        Assert.AreEqual(HttpStatusCode.OK, operations.StatusCode);
        Assert.HasCount(0, (await operations.Content.ReadFromJsonAsync<ConnectedServiceOperationResource[]>())!);

        SelectConsoleContext(client, workspace);
        using var forbiddenRequest = new HttpRequestMessage(HttpMethod.Post, prefix.Replace("?", "/deactivate?", StringComparison.Ordinal));
        forbiddenRequest.Headers.IfMatch.Add(etag);
        using var forbidden = await client.SendAsync(forbiddenRequest);
        Assert.IsTrue(forbidden.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        SelectConsoleContext(client, tenant);

        using var staleRequest = new HttpRequestMessage(HttpMethod.Post, prefix.Replace("?", "/deactivate?", StringComparison.Ordinal));
        staleRequest.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");
        using var stale = await client.SendAsync(staleRequest);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        using var update = new HttpRequestMessage(HttpMethod.Put, prefix) { Content = JsonContent.Create(new PutConnectedServiceRequest(properties with { DisplayName = "Updated exact service" })) };
        update.Headers.IfMatch.Add(etag);
        using var updated = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        Assert.AreEqual(namespaced.Uid, (await updated.Content.ReadFromJsonAsync<ConnectedServiceResource>())!.Uid);
        using var reconcile = new HttpRequestMessage(HttpMethod.Post, prefix.Replace("?", "/reconcile?", StringComparison.Ordinal));
        reconcile.Headers.IfMatch.Add(updated.Headers.ETag!);
        using var reconciled = await client.SendAsync(reconcile);
        Assert.AreEqual(HttpStatusCode.OK, reconciled.StatusCode);
        using var deactivate = new HttpRequestMessage(HttpMethod.Post, prefix.Replace("?", "/deactivate?", StringComparison.Ordinal));
        deactivate.Headers.IfMatch.Add(reconciled.Headers.ETag!);
        using var deactivated = await client.SendAsync(deactivate);
        Assert.AreEqual(HttpStatusCode.OK, deactivated.StatusCode);
        var disabled = (await deactivated.Content.ReadFromJsonAsync<ConnectedServiceResource>())!;
        Assert.AreEqual(namespaced.Uid, disabled.Uid);
        Assert.IsFalse(disabled.Definition.Enabled);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, prefix);
        delete.Headers.IfMatch.Add(deactivated.Headers.ETag!);
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        using var absent = await client.GetAsync(prefix);
        Assert.AreEqual(HttpStatusCode.NotFound, absent.StatusCode);
        var untouched = (await client.GetFromJsonAsync<ConnectedServiceResource>($"/api/connected-services/same-service?scopeRef={Uri.EscapeDataString(tenant.Value)}"))!;
        Assert.AreEqual(defaultResource.Uid, untouched.Uid);
        Assert.AreEqual(defaultResource.Generation, untouched.Generation);
        Assert.IsTrue(untouched.Definition.Enabled);
    }
}
