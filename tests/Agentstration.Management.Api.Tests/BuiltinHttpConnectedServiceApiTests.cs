using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Parameters;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class ConnectedServiceApiTests
{
    [TestMethod]
    public async Task UnreleasedHttpBaselineGainsDiscoveryWithoutReplacingItsIdentityOrUsePolicy()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        using var core = factory.Services.GetRequiredService<IResourceGovernanceContext>()
            .Push(ResourceGovernance.Core("connected-services.http", "1.0.0"));
        var address = new ScopedResourceAddress(ResourceScopeRef.Instance, ResourceNamespace.Default,
            ConnectedServiceResourceKinds.ConnectedServiceType, "http-endpoint");
        var current = (await store.GetExactAsync<ConnectedServiceTypeResource>(address, default))!;
        var legacy = current.Value with
        {
            Definition = current.Value.Definition with
            {
                UsePolicy = new() { Grants = [new(ResourceScopeRef.Workspace(context.WorkspaceId))] },
                Versions = [current.Value.Definition.Versions.Single() with { OperationDiscovery = null }]
            }
        };
        _ = await store.PutExactAsync(ResourceScopeRef.Instance, legacy, current.ETag, false, default);
        var provisioner = factory.Services.GetRequiredService<ConnectedServicePlatformResourceProvisioner>();
        await provisioner.EnsureAsync(default);
        var reconciled = (await store.GetExactAsync<ConnectedServiceTypeResource>(address, default))!;
        Assert.AreEqual(legacy.Uid, reconciled.Value.Uid);
        Assert.AreEqual("1.0.0", reconciled.Value.Definition.PreferredVersion);
        Assert.HasCount(1, reconciled.Value.Definition.Versions);
        Assert.IsNotNull(reconciled.Value.Definition.Versions.Single().OperationDiscovery);
        Assert.IsTrue(reconciled.Value.Definition.UsePolicy.Grants.SequenceEqual(legacy.Definition.UsePolicy.Grants));
        await provisioner.EnsureAsync(default);
        Assert.AreEqual(reconciled.ETag, (await store.GetExactAsync<ConnectedServiceTypeResource>(address, default))!.ETag);
    }

    [TestMethod]
    [DataRow("instance", false)]
    [DataRow("workspace", false)]
    [DataRow("instance-shared", false)]
    [DataRow("instance", true)]
    public async Task BuiltinHttpDiscoveryAndAcquisitionUseOnlyIndependentDatasetParameters(string ownerKind, bool boundInputs)
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
        var owner = ownerKind is "instance" or "instance-shared" ? ResourceScopeRef.Instance : ResourceScopeRef.Workspace(context.WorkspaceId);
        var consumer = ownerKind == "instance-shared" ? ResourceScopeRef.Workspace(context.WorkspaceId) : owner;
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
                using var core = factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("connected-services.http", "1.0.0"));
                // Explicit fixture grants: production provisioning must preserve access decisions.
                var typeStored = (await store.GetExactAsync<ConnectedServiceTypeResource>(new(type.ScopeRef!.Value,
                    type.Namespace, type.Kind, type.Name), default))!;
                type = (await store.PutExactAsync(type.ScopeRef.Value, type with
                {
                    Definition = type.Definition with
                    { UsePolicy = new() { Grants = [new(owner)] } }
                }, typeStored.ETag, false, default)).Value;
                var operationType = (await store.GetExactAsync<ConnectedServiceOperationTypeResource>(new(ResourceScopeRef.Instance,
                    ResourceNamespace.Default, ConnectedServiceResourceKinds.ConnectedServiceOperationType, "http-get"), default))!;
                _ = await store.PutExactAsync(ResourceScopeRef.Instance, operationType.Value with
                {
                    Definition = operationType.Value.Definition with
                    { UsePolicy = new() { Grants = [new(owner)] } }
                }, operationType.ETag, false, default);
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
            Type = new()
            {
                Type = new(type.Name, type.ScopeRef, type.Namespace),
                TypeUid = type.Uid,
                Version = version.Version,
                SchemaDigest = version.SchemaDigest
            },
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
        if (ownerKind == "instance-shared")
        {
            using (scopes.PushSystem())
            {
                var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
                _ = await policies.PutAsync(new(new(service.Uid, owner, service.Namespace, service.Kind),
                    [new(consumer, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
                _ = await policies.PutAsync(new(new(storage.Uid, owner, storage.Namespace, storage.Kind),
                    [new(consumer, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
                var localType = (await store.GetExactAsync<StorageTypeResource>(new(ResourceScopeRef.Instance,
                    ResourceNamespace.Default, StorageResourceKinds.StorageType, "local"), default))!.Value;
                _ = await policies.PutAsync(new(new(localType.Uid, ResourceScopeRef.Instance, localType.Namespace, localType.Kind),
                    [new(consumer, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
            }
            SelectConsoleContext(client, consumer);
            Assert.IsFalse((await client.GetFromJsonAsync<ConnectedServiceOperationTypeResource[]>("/api/connected-service-operation-types", timeout.Token))!.Any(t => t.Uid == operation.Definition.Type.TypeUid));
            using (scopes.PushSystem())
            {
                var adapter = operation.Definition.Type;
                var operationType = (await store.GetExactAsync<ConnectedServiceOperationTypeResource>(new(adapter.Type.ScopeRef!.Value,
                    adapter.Type.Namespace ?? operation.Namespace, ConnectedServiceResourceKinds.ConnectedServiceOperationType, adapter.Type.Name), timeout.Token))!.Value;
                var acquisition = operationType.Definition.Versions.Single(v => v.Version == adapter.Version).Acquisition!.Flow;
                Assert.IsFalse((await factory.Services.GetRequiredService<IResourceAccessPolicyService>().EvaluateAsync(
                    new(acquisition.FlowUid, acquisition.Flow.ScopeRef!.Value, acquisition.Flow.Namespace ?? ResourceNamespace.Default,
                        FlowResourceKinds.Flow, Version: acquisition.Version), consumer, ResourceAccessAction.Use, timeout.Token)).Allowed);
            }
            using var contract = await client.PostAsJsonAsync("/api/connected-service-operations/contract", pin, timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, contract.StatusCode, await contract.Content.ReadAsStringAsync(timeout.Token));
        }
        using var invalid = await client.PostAsJsonAsync("/api/datasources", new CreateDataSourceRequest("http-invalid",
            new() { DisplayName = "Invalid", Dataset = new(storagePin, new(Write: true), new(pin, JsonSerializer.SerializeToElement(new { q = 42 }))) }, ScopeRef: consumer));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var results = new List<DataSourceAcquisitionResource>();
        IReadOnlyList<DataSourceParameterBinding> parameterBindings = [], secretBindings = [];
        if (boundInputs)
        {
            using var request = scopes.PushInstance(context);
            var parameter = await factory.Services.GetRequiredService<ParameterManagementService>().CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = ParameterResourceKinds.Parameter,
                Metadata = new() { Name = "acquisition-query" }, ScopeRef = consumer,
                Definition = new() { DisplayName = "Query", ValueType = ParameterValueType.Text,
                    Value = JsonSerializer.SerializeToElement("Rennes when:1d & été+news") }
            }, timeout.Token);
            parameterBindings = [new("q", Parameter: new(parameter.Value.Address, consumer))];
            var secrets = factory.Services.GetRequiredService<SecretManagementService>();
            await secrets.CreateVaultAsync(new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = SecretResourceKinds.Vault,
                Metadata = new() { Name = "acquisition-vault" }, ScopeRef = consumer,
                Definition = new() { DisplayName = "Acquisition vault", ProviderType = "local" } }, timeout.Token);
            await secrets.InitializeVaultExactAsync(consumer, "acquisition-vault", timeout.Token);
            var secret = await secrets.CreateSecretAsync(new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = SecretResourceKinds.Secret,
                Metadata = new() { Name = "acquisition-secret" }, ScopeRef = consumer,
                Definition = new() { DisplayName = "Query", Vault = new("acquisition-vault", consumer), Key = "query" } }, timeout.Token);
            using var value = new SecretValue(Encoding.UTF8.GetBytes("Brest"));
            await secrets.SetValueExactAsync(consumer, secret.Value.Name, value, timeout.Token);
            secretBindings = [new("q", Secret: new(secret.Value.Address, consumer))];
        }
        foreach (var (name, parameters) in new[]
        {
            ("rennes", JsonSerializer.SerializeToElement(new { q = "Rennes when:1d & été+news", hl = "fr", gl = "FR", ceid = "FR:fr" })),
            ("brest", JsonSerializer.SerializeToElement(new { q = "Brest" })),
            ("empty", JsonSerializer.SerializeToElement(new { }))
        })
        {
            var bindings = !boundInputs ? [] : name == "rennes" ? parameterBindings : name == "brest" ? secretBindings : [];
            var literals = bindings.Count == 0 ? parameters : JsonSerializer.SerializeToElement(parameters.EnumerateObject()
                .Where(property => property.Name != "q").ToDictionary(property => property.Name, property => property.Value));
            using var saved = await client.PostAsJsonAsync("/api/datasources", new CreateDataSourceRequest("http-" + name,
                new() { DisplayName = name, Dataset = new(storagePin, new(Write: true), new(pin, literals) { ParameterBindings = bindings }) }, ScopeRef: consumer), timeout.Token);
            Assert.AreEqual(HttpStatusCode.Created, saved.StatusCode, await saved.Content.ReadAsStringAsync(timeout.Token));
            Assert.DoesNotContain("Nantes", await saved.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
            using var start = await client.PostAsJsonAsync($"/api/datasources/http-{name}/acquisitions?scopeRef={Uri.EscapeDataString(consumer.Value)}",
                new StartDataSourceAcquisitionRequest(), timeout.Token);
            Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode, await start.Content.ReadAsStringAsync(timeout.Token));
            var acquisition = (await start.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>(timeout.Token))!;
            do
            {
                await Task.Delay(25, timeout.Token);
                acquisition = (await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{acquisition.Name}", timeout.Token))!;
            } while (acquisition.CompletedAt is null);
            Assert.AreEqual(DataSourceAcquisitionState.Succeeded, acquisition.State, $"{acquisition.ErrorCode}: {acquisition.ErrorMessage}");
            if (boundInputs && name == "brest")
            {
                Assert.IsTrue(SecretInputs.IsReference(acquisition.Parameters.GetProperty("q"), acquisition.Dataset!.SecretInputs));
                Assert.DoesNotContain("Brest", JsonSerializer.Serialize(acquisition));
                Assert.DoesNotContain("Brest", await saved.Content.ReadAsStringAsync(timeout.Token));
            }
            else Assert.IsTrue(JsonElement.DeepEquals(parameters, acquisition.Parameters));
            using var content = await client.GetAsync($"/api/datasourceacquisitions/{acquisition.Name}/content", timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, content.StatusCode, await content.Content.ReadAsStringAsync(timeout.Token));
            var storedContent = (await content.Content.ReadFromJsonAsync<StorageObjectContent>(timeout.Token))!;
            var records = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(storedContent.ContentBase64));
            Assert.AreEqual(1, records.GetArrayLength());
            var output = records[0];
            Assert.AreEqual("application/rss+xml", output.GetProperty("mediaType").GetString());
            Assert.Contains("<rss>", output.GetProperty("content").GetString()!);
            results.Add(acquisition);
        }
        Assert.HasCount(3, requests);
        var urls = requests.ToArray();
        if (boundInputs)
        {
            using var request = scopes.PushInstance(context);
            Assert.IsTrue((await factory.Services.GetRequiredService<ParameterManagementService>()
                .GetUsagesAsync(consumer, "acquisition-query", timeout.Token)).Any(usage => usage.Name == "http-rennes"));
            Assert.IsTrue((await factory.Services.GetRequiredService<SecretManagementService>()
                .GetSecretUsagesAsync(consumer, "acquisition-secret", timeout.Token)).Any(usage => usage.Name == "http-brest"));
            var usages = await client.GetFromJsonAsync<Agentstration.Secrets.Contracts.SecretUsagesResponse>(
                "/api/secrets/acquisition-secret/usages?scopeRef=/instance", timeout.Token);
            Assert.Contains("/data-sources/http-brest?", usages!.Value.Single(usage => usage.Name == "http-brest").Url);
            Assert.Contains("context=%2Finstance", usages.Value.Single(usage => usage.Name == "http-brest").Url);
        }
        Assert.AreEqual("?ceid=FR%3Afr&gl=FR&hl=fr&q=Rennes%20when%3A1d%20%26%20%C3%A9t%C3%A9%2Bnews", urls[0].Query);
        Assert.AreEqual("?q=Brest", urls[1].Query);
        Assert.AreEqual("", urls[2].Query);
        Assert.HasCount(3, results.Select(result => result.Content!.Key).Distinct());
        SelectConsoleContext(client, owner);
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
