using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Storage;
using Agentstration.Resources;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class ConnectedServiceApiTests
{
    [TestMethod]
    [DataRow("instance")]
    [DataRow("workspace")]
    public async Task BuiltinHttpDiscoveryAndAcquisitionUseOnlyIndependentDatasetParameters(string ownerKind)
    {
        var requests = new ConcurrentQueue<Uri>();
        await using var factory = Factory().WithWebHostBuilder(builder =>
        {
            AwpTestWorker.Configure(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHttpContentFetcher>();
                services.AddSingleton<IHttpContentFetcher>(_ => new SafeHttpContentFetcher(new HttpClient(new RssHandler(requests))));
            });
        });
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        var owner = ownerKind == "instance" ? ResourceScopeRef.Instance : ResourceScopeRef.Workspace(context.WorkspaceId);
        SelectConsoleContext(client, owner);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        ConnectedServiceTypeResource type;
        StorageResource storage;
        using (scopes.PushSystem())
        {
            type = (await store.GetExactAsync<ConnectedServiceTypeResource>(new(ResourceScopeRef.Instance,
                ResourceNamespace.Default, ConnectedServiceResourceKinds.ConnectedServiceType, "http-endpoint"), default))!.Value;
            if (owner != ResourceScopeRef.Instance)
            {
                using var core = factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("connected-services.http", "1.1.0"));
                // Explicit fixture grants: production provisioning must preserve access decisions.
                var typeStored = (await store.GetExactAsync<ConnectedServiceTypeResource>(new(type.ScopeRef!.Value,
                    type.Namespace, type.Kind, type.Name), default))!;
                type = (await store.PutExactAsync(type.ScopeRef.Value, type with { Definition = type.Definition with
                    { UsePolicy = new() { Grants = [new(owner)] } } }, typeStored.ETag, false, default)).Value;
                var operationType = (await store.GetExactAsync<ConnectedServiceOperationTypeResource>(new(ResourceScopeRef.Instance,
                    ResourceNamespace.Default, ConnectedServiceResourceKinds.ConnectedServiceOperationType, "http-get"), default))!;
                _ = await store.PutExactAsync(ResourceScopeRef.Instance, operationType.Value with { Definition = operationType.Value.Definition with
                    { UsePolicy = new() { Grants = [new(owner)] } } }, operationType.ETag, false, default);
                var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
                var localType = (await store.ListExactAsync<StorageTypeResource>(ResourceScopeRef.Instance,
                    StorageResourceKinds.StorageType, 0, 100, default)).Single(item => item.Value.Name == "local").Value;
                foreach (var resource in new Resource[] { type, operationType.Value, localType })
                    _ = await policies.PutAsync(new(new(resource.Uid, ResourceScopeRef.Instance, resource.Namespace, resource.Kind),
                        [new(owner, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
                var flowService = factory.Services.GetRequiredService<FlowService>();
                foreach (var flowName in new[] { "http-operation-discovery-builtin", "http-operation-acquisition-builtin" })
                {
                    var flow = (await flowService.GetExactAsync(ResourceScopeRef.Instance, new FlowId(flowName), default))!.Value;
                    _ = await policies.PutAsync(new(new(flow.Uid, ResourceScopeRef.Instance, ResourceNamespace.Default, FlowResourceKinds.Flow),
                        [new(owner, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
                }
                await factory.Services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
            }
            storage = (await store.GetExactAsync<StorageResource>(new(owner, ResourceNamespace.Default,
                StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))!.Value;
        }
        var version = type.Definition.Versions.Single(item => item.Version == type.Definition.PreferredVersion);
        var properties = new ConnectedServiceProperties
        {
            DisplayName = "News HTTP",
            Type = new() { Type = new(type.Name, type.ScopeRef, type.Namespace), TypeUid = type.Uid,
                Version = version.Version, SchemaDigest = version.SchemaDigest },
            Options = JsonSerializer.SerializeToElement(new { }),
            Bindings = [new() { InputId = "baseUrl", Kind = ConnectedServiceBindingKind.Literal,
                Literal = JsonSerializer.SerializeToElement("https://news.example.test/rss/search?q=Nantes+when:1d&hl=fr&gl=FR&ceid=FR:fr") }]
        };
        using var created = await client.PostAsJsonAsync("/api/connected-services", new CreateConnectedServiceRequest("http-news", properties, owner));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var service = (await created.Content.ReadFromJsonAsync<ConnectedServiceResource>())!;
        await using var worker = await AwpTestWorker.StartAsync(factory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        async Task<ConnectedServiceOperationDiscoveryResource> DiscoverAsync()
        {
            using var start = await client.PostAsJsonAsync($"/api/connected-services/http-news/operation-discoveries?scopeRef={Uri.EscapeDataString(owner.Value)}",
                new StartConnectedServiceOperationDiscoveryRequest(), timeout.Token);
            Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode, await start.Content.ReadAsStringAsync(timeout.Token));
            var discovery = (await start.Content.ReadFromJsonAsync<ConnectedServiceOperationDiscoveryResource>(timeout.Token))!;
            do
            {
                await Task.Delay(25, timeout.Token);
                discovery = (await client.GetFromJsonAsync<ConnectedServiceOperationDiscoveryResource>(
                    $"/api/connected-service-operation-discoveries/{discovery.Name}", timeout.Token))!;
            } while (discovery.CompletedAt is null);
            Assert.AreEqual(ConnectedServiceOperationDiscoveryState.Succeeded, discovery.State, $"{discovery.ErrorCode}: {discovery.ErrorMessage}");
            Assert.DoesNotContain("Nantes", JsonSerializer.Serialize(discovery), StringComparison.Ordinal);
            return discovery;
        }
        _ = await DiscoverAsync();
        Assert.IsEmpty(requests);
        var operationsUrl = $"/api/connected-services/http-news/operations?scopeRef={Uri.EscapeDataString(owner.Value)}";
        var operation = (await client.GetFromJsonAsync<ConnectedServiceOperationResource[]>(operationsUrl, timeout.Token))!.Single();
        Assert.IsFalse(operation.Enabled);
        Assert.AreEqual("http-get", operation.Definition.Type.Type.Name);
        CollectionAssert.AreEqual(new[] { "ceid", "gl", "hl", "q" }, operation.Definition.Definition.GetProperty("inputSchema")
            .GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray());
        var operationUrl = $"/api/connected-service-operations/{operation.Name}?scopeRef={Uri.EscapeDataString(owner.Value)}";
        using var getOperation = await client.GetAsync(operationUrl, timeout.Token);
        using var enable = new HttpRequestMessage(HttpMethod.Put,
            $"/api/connected-service-operations/{operation.Name}/enabled?scopeRef={Uri.EscapeDataString(owner.Value)}")
        { Content = JsonContent.Create(new SetConnectedServiceOperationEnabledRequest(true)) };
        enable.Headers.IfMatch.Add(getOperation.Headers.ETag!);
        using var enabled = await client.SendAsync(enable, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode, await enabled.Content.ReadAsStringAsync(timeout.Token));
        operation = (await enabled.Content.ReadFromJsonAsync<ConnectedServiceOperationResource>(timeout.Token))!;
        var pin = new ConnectedServiceOperationRevisionReference(new(owner, operation.Namespace, operation.Kind, operation.Name),
            operation.Uid, operation.Generation, operation.Definition.DefinitionDigest);
        var storagePin = new StorageRevisionReference(new(owner, storage.Namespace, storage.Kind, storage.Name),
            storage.Uid, storage.Generation, storage.ObservedState!.BindingDigest!);
        using var invalid = await client.PostAsJsonAsync("/api/datasources", new CreateDataSourceRequest("http-invalid",
            new() { DisplayName = "Invalid", Dataset = new(storagePin, new(Write: true), new(pin, JsonSerializer.SerializeToElement(new { q = 42 }))) }, ScopeRef: owner));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var results = new List<DataSourceAcquisitionResource>();
        foreach (var (name, parameters) in new[]
        {
            ("rennes", JsonSerializer.SerializeToElement(new { q = "Rennes when:1d & été+news", hl = "fr", gl = "FR", ceid = "FR:fr" })),
            ("brest", JsonSerializer.SerializeToElement(new { q = "Brest" })),
            ("empty", JsonSerializer.SerializeToElement(new { }))
        })
        {
            using var saved = await client.PostAsJsonAsync("/api/datasources", new CreateDataSourceRequest("http-" + name,
                new() { DisplayName = name, Dataset = new(storagePin, new(Write: true), new(pin, parameters)) }, ScopeRef: owner), timeout.Token);
            Assert.AreEqual(HttpStatusCode.Created, saved.StatusCode, await saved.Content.ReadAsStringAsync(timeout.Token));
            Assert.DoesNotContain("Nantes", await saved.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
            using var start = await client.PostAsJsonAsync($"/api/datasources/http-{name}/acquisitions?scopeRef={Uri.EscapeDataString(owner.Value)}",
                new StartDataSourceAcquisitionRequest(), timeout.Token);
            Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode, await start.Content.ReadAsStringAsync(timeout.Token));
            var acquisition = (await start.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>(timeout.Token))!;
            do
            {
                await Task.Delay(25, timeout.Token);
                acquisition = (await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{acquisition.Name}", timeout.Token))!;
            } while (acquisition.CompletedAt is null);
            Assert.AreEqual(DataSourceAcquisitionState.Succeeded, acquisition.State, $"{acquisition.ErrorCode}: {acquisition.ErrorMessage}");
            Assert.IsTrue(JsonElement.DeepEquals(parameters, acquisition.Parameters));
            using var content = await client.GetAsync($"/api/datasourceacquisitions/{acquisition.Name}/content", timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, content.StatusCode, await content.Content.ReadAsStringAsync(timeout.Token));
            var storedContent = (await content.Content.ReadFromJsonAsync<StorageObjectContent>(timeout.Token))!;
            var output = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(storedContent.ContentBase64));
            Assert.AreEqual("application/rss+xml", output.GetProperty("mediaType").GetString());
            Assert.Contains("<rss>", output.GetProperty("content").GetString()!);
            results.Add(acquisition);
        }
        Assert.HasCount(3, requests);
        var urls = requests.ToArray();
        Assert.AreEqual("?ceid=FR%3Afr&gl=FR&hl=fr&q=Rennes%20when%3A1d%20%26%20%C3%A9t%C3%A9%2Bnews", urls[0].Query);
        Assert.AreEqual("?q=Brest", urls[1].Query);
        Assert.AreEqual("", urls[2].Query);
        Assert.HasCount(3, results.Select(result => result.Content!.Key).Distinct());
        _ = await DiscoverAsync();
        var rediscovered = (await client.GetFromJsonAsync<ConnectedServiceOperationResource[]>(operationsUrl, timeout.Token))!.Single();
        Assert.AreEqual(operation.Uid, rediscovered.Uid);
        Assert.AreEqual(operation.Generation + 1, rediscovered.Generation);
        Assert.AreEqual(operation.Definition.DefinitionDigest, rediscovered.Definition.DefinitionDigest);
        Assert.IsTrue(rediscovered.Enabled);
        Assert.HasCount(3, requests);
        Assert.AreEqual(service.Generation, rediscovered.Definition.Service.Generation);
    }

    private sealed class RssHandler(ConcurrentQueue<Uri> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<rss><channel><title>Fixture</title></channel></rss>", Encoding.UTF8, "application/rss+xml") });
        }
    }
}
