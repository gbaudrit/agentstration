extern alias openapi;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Storage;
using Agentstration.Infrastructure.Tools;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Contracts;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenApi = openapi::Agentstration.Extensions.OpenApi;

namespace Agentstration.Management.Tests;

public sealed partial class ConnectedServiceApiTests
{
    [TestMethod]
    [DataRow("success", "workspace", "parameter")]
    [DataRow("changed-generation", "workspace", "parameter")]
    [DataRow("disabled", "workspace", "parameter")]
    [DataRow("success", "instance", "parameter")]
    [DataRow("changed-generation", "instance", "parameter")]
    [DataRow("disabled", "instance", "parameter")]
    [DataRow("success", "tenant", "parameter")]
    [DataRow("changed-generation", "tenant", "parameter")]
    [DataRow("disabled", "tenant", "parameter")]
    [DataRow("success", "workspace", "literal")]
    [DataRow("success", "workspace", "literal-no-secret")]
    [DataRow("success", "instance", "literal")]
    [DataRow("success", "instance", "literal-no-secret")]
    [DataRow("success", "tenant", "literal")]
    [DataRow("success", "tenant", "literal-no-secret")]
    public async Task OpenApiPublishedDiscoveryFlowUsesExactServiceAndSecretCallbackAndMaterializesOperations(string scenario, string ownerKind, string baseBinding)
    {
        const string credential = "synthetic-openapi-connection-credential";
        var calls = 0;
        var providerCalls = 0;
        var secretCallbacks = 0;
        WebApplicationFactory<Program>? authority = null;
        await using var extension = new WebApplicationFactory<openapi::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenApi:AllowedDomains:0", "example.test");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<OpenApi.IOpenApiAddressResolver>();
                services.AddSingleton<OpenApi.IOpenApiAddressResolver, PublicAddressResolver>();
                services.AddHttpClient<OpenApi.OpenApiService>().ConfigurePrimaryHttpMessageHandler(() => new ContractHandler(() =>
                    Interlocked.Increment(ref calls), baseBinding == "literal-no-secret" ? null : credential));
                services.AddHttpClient("openapi-secret-access").ConfigurePrimaryHttpMessageHandler(() =>
                    new SecretCallbackHandler(authority!.Server.CreateHandler(), () => Interlocked.Increment(ref secretCallbacks)));
            });
        });
        await using var factory = Factory().WithWebHostBuilder(builder =>
        {
            AwpTestWorker.Configure(builder);
            builder.UseSetting("Agentstration:Aep:SecretAccess:PublicBaseUrl", "https://localhost");
            builder.ConfigureTestServices(services =>
            {
                foreach (var name in new[] { "agentstration-aep", "agentstration-aep-tools", "agentstration-aep-mcp-tools", "agentstration-mcp-tools" })
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() =>
                        new DiscoveryRequestHandler(extension.Server.CreateHandler(), () => Interlocked.Increment(ref providerCalls)));
            });
        });
        authority = factory;
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        var owner = ownerKind switch
        {
            "instance" => ResourceScopeRef.Instance,
            "tenant" => ResourceScopeRef.Tenant(context.TenantId),
            _ => await WorkspaceScopeAsync(client)
        };
        SelectConsoleContext(client, owner);
        var manifest = await new AepClient(extension.CreateClient()).DiscoverAsync();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using (scopes.PushSystem())
        {
            var registration = (await store.PutExactAsync(owner, new ExtensionRegistrationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.ExtensionRegistration,
                Metadata = new() { Name = "openapi-test" },
                ScopeRef = owner,
                Definition = new()
                {
                    DisplayName = "OpenAPI test",
                    Endpoint = new("https://extension.example.test"),
                    ExpectedExtensionId = manifest.Extension.Id
                }
            }, null, true, default)).Value;
            foreach (var provisioner in factory.Services.GetServices<IAepEnrollmentResourceProvisioner>())
                await provisioner.ProvisionAsync(registration, manifest, default);
            var installed = await store.ListExactAsync<ExtensionBundleInstallationResource>(owner,
                ExtensionKinds.ExtensionBundleInstallation, 0, 10, default);
            Assert.HasCount(2, installed);
            foreach (var provisioner in factory.Services.GetServices<IAepEnrollmentResourceProvisioner>())
                await provisioner.ProvisionAsync(registration, manifest, default);
            var repeated = await store.ListExactAsync<ExtensionBundleInstallationResource>(owner,
                ExtensionKinds.ExtensionBundleInstallation, 0, 10, default);
            CollectionAssert.AreEquivalent(installed.Select(item => item.ETag).ToArray(), repeated.Select(item => item.ETag).ToArray());
        }
        using var parameter = await client.PostAsJsonAsync("/api/parameters", new CreateParameterRequest("openapi-origin",
            new()
            {
                DisplayName = "OpenAPI origin",
                ValueType = ParameterValueType.Text,
                Value = JsonSerializer.SerializeToElement("https://origin.example.test")
            }, owner));
        Assert.AreEqual(HttpStatusCode.Created, parameter.StatusCode);
        using var vault = await client.PostAsJsonAsync("/api/vaults", new CreateVaultRequest("openapi-vault",
            new() { DisplayName = "OpenAPI Vault", ProviderType = "local" }, owner));
        Assert.AreEqual(HttpStatusCode.Created, vault.StatusCode);
        using (scopes.PushSystem())
            await factory.Services.GetRequiredService<SecretManagementService>().InitializeVaultExactAsync(owner, "openapi-vault", default);
        using var secret = await client.PostAsJsonAsync("/api/secrets", new CreateSecretRequest("openapi-token",
            new() { DisplayName = "OpenAPI token", Vault = new("openapi-vault", owner), Key = "token" }, owner));
        Assert.AreEqual(HttpStatusCode.Created, secret.StatusCode);
        using var set = await client.PutAsJsonAsync($"/api/secrets/openapi-token/value?scopeRef={Uri.EscapeDataString(owner.Value)}",
            new SetSecretValueRequest(credential));
        Assert.AreEqual(HttpStatusCode.NoContent, set.StatusCode);
        ConnectedServiceTypeResource type;
        using (scopes.PushSystem())
            type = (await factory.Services.GetRequiredService<ConnectedServiceTypeManagementService>()
                .GetExactAsync(owner, ResourceNamespace.Parse(manifest.Extension.Id), "openapi", default))!.Value;
        var version = type.Definition.Versions.Single();
        var bindings = new List<ConnectedServiceValueBinding>
        {
            baseBinding == "parameter"
                ? new() { InputId = "baseUrl", Kind = ConnectedServiceBindingKind.Parameter,
                    Parameter = new(ResourceAddress.Create(ResourceNamespace.Default, ParameterResourceKinds.Parameter, "openapi-origin"), owner) }
                : new() { InputId = "baseUrl", Kind = ConnectedServiceBindingKind.Literal,
                    Literal = JsonSerializer.SerializeToElement("https://origin.example.test") }
        };
        if (baseBinding != "literal-no-secret")
            bindings.Add(new()
            {
                InputId = "bearerToken",
                Kind = ConnectedServiceBindingKind.Secret,
                Secret = new(ResourceAddress.Create(ResourceNamespace.Default, SecretResourceKinds.Secret, "openapi-token"), owner)
            });
        using var created = await client.PostAsJsonAsync("/api/connected-services", new CreateConnectedServiceRequest("orders",
            new()
            {
                DisplayName = "Orders",
                Type = new()
                {
                    Type = new("openapi", owner, type.Namespace),
                    TypeUid = type.Uid,
                    Version = version.Version,
                    SchemaDigest = version.SchemaDigest
                },
                Options = JsonSerializer.SerializeToElement(new { candidatePaths = new[] { "/openapi.json" } }),
                Bindings = bindings
            }, owner));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var service = (await created.Content.ReadFromJsonAsync<ConnectedServiceResource>())!;
        if (baseBinding != "parameter")
        {
            using var invalid = await client.PostAsJsonAsync("/api/connected-services", new CreateConnectedServiceRequest("invalid-inline-credential",
                service.Definition with
                {
                    Bindings = [bindings[0], new() { InputId = "bearerToken", Kind = ConnectedServiceBindingKind.Literal,
                    Literal = JsonSerializer.SerializeToElement(credential) }]
                }, owner));
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }
        using var start = await client.PostAsJsonAsync($"/api/connected-services/orders/operation-discoveries?scopeRef={Uri.EscapeDataString(owner.Value)}",
            new StartConnectedServiceOperationDiscoveryRequest(IdempotencyKey: "openapi-real-flow"));
        Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode, await start.Content.ReadAsStringAsync());
        var discovery = (await start.Content.ReadFromJsonAsync<ConnectedServiceOperationDiscoveryResource>())!;
        context = new(discovery.CreatedBy, discovery.TenantId, discovery.WorkspaceId);
        using (ownerKind switch
        {
            "instance" => scopes.PushInstance(context),
            "tenant" => scopes.PushTenantAdministration(context),
            _ => scopes.Push(context)
        })
        {
            var executor = factory.Services.GetRequiredService<ConnectedServiceDiscoveryExecutor>();
            var invocation = new InternalMcpToolInvocation(context.TenantId, new(context.WorkspaceId), context.PrincipalId,
                "forged-call", "test", JsonSerializer.SerializeToElement(new { discoveryId = discovery.Name, discoveryNamespace = discovery.Namespace.Value }),
                ToolDefinitionCallerKind.Flow, RunId: discovery.FlowRunId);
            foreach (var invalid in new[]
            {
                invocation with { WorkspaceId = new(Guid.NewGuid()) },
                invocation with { TenantId = Guid.NewGuid() },
                invocation with { PrincipalId = Guid.NewGuid() },
                invocation with { CallerKind = ToolDefinitionCallerKind.Agent },
                invocation with { RunId = null }
            })
            {
                var denied = await Assert.ThrowsAsync<ConnectedServiceOperationDiscoveryException>(() =>
                    executor.ExecuteAsync(invalid, default));
                Assert.AreEqual("connected_service_discovery_execution_denied", denied.Code);
            }
            using (ownerKind == "workspace" ? scopes.PushInstance(context) : scopes.Push(context))
            {
                var wrongScope = await Assert.ThrowsAsync<ConnectedServiceOperationDiscoveryException>(() =>
                    executor.ExecuteAsync(invocation, default));
                Assert.AreEqual("connected_service_discovery_execution_denied", wrongScope.Code);
            }
            var wrongRun = await Assert.ThrowsAsync<ConnectedServiceOperationDiscoveryException>(() =>
                executor.ExecuteAsync(invocation with { RunId = "another-run" }, default));
            Assert.AreEqual("connected_service_discovery_execution_denied", wrongRun.Code);
            if (scenario != "success")
                await factory.Services.GetRequiredService<ConnectedServiceManagementService>().PutExactAsync(owner, "orders",
                    service.Definition with { Enabled = scenario != "disabled" }, created.Headers.ETag!.Tag, default);
        }
        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, providerCalls);
        await using var worker = await AwpTestWorker.StartAsync(factory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        do
        {
            await Task.Delay(25, timeout.Token);
            discovery = (await client.GetFromJsonAsync<ConnectedServiceOperationDiscoveryResource>(
                $"/api/connected-service-operation-discoveries/{discovery.Name}", timeout.Token))!;
        } while (discovery.CompletedAt is null);
        if (scenario != "success")
        {
            Assert.AreEqual(ConnectedServiceOperationDiscoveryState.Failed, discovery.State);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(0, providerCalls);
            return;
        }
        Assert.AreEqual(ConnectedServiceOperationDiscoveryState.Succeeded, discovery.State, $"{discovery.ErrorCode}: {discovery.ErrorMessage}");
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, providerCalls);
        Assert.AreEqual(baseBinding == "literal-no-secret" ? 0 : 1, secretCallbacks);
        var operations = (await client.GetFromJsonAsync<ConnectedServiceOperationResource[]>(
            $"/api/connected-services/orders/operations?scopeRef={Uri.EscapeDataString(owner.Value)}"))!;
        Assert.HasCount(1, operations);
        Assert.AreEqual("openapi-operation", operations[0].Definition.Type.Type.Name);
        Assert.AreEqual(version.OperationDiscovery!.ProducedTypes.Single(), operations[0].Definition.Type);
        Assert.AreEqual(service.Uid, operations[0].Definition.Service.ServiceUid);
        Assert.AreEqual(service.Generation, operations[0].Definition.Service.Generation);
        Assert.AreEqual("getOrders", operations[0].Definition.Definition.GetProperty("operationId").GetString());
        var selected = operations[0];
        using var operationGet = await client.GetAsync($"/api/connected-service-operations/{selected.Name}?namespace={selected.Namespace}&scopeRef={Uri.EscapeDataString(owner.Value)}");
        using var enabledResponse = new HttpRequestMessage(HttpMethod.Put,
            $"/api/connected-service-operations/{selected.Name}/enabled?namespace={selected.Namespace}&scopeRef={Uri.EscapeDataString(owner.Value)}")
        { Content = JsonContent.Create(new { enabled = true }) };
        enabledResponse.Headers.IfMatch.Add(operationGet.Headers.ETag!);
        using var enabled = await client.SendAsync(enabledResponse);
        Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode, await enabled.Content.ReadAsStringAsync());
        selected = (await enabled.Content.ReadFromJsonAsync<ConnectedServiceOperationResource>())!;
        StorageResource storage;
        using (scopes.PushSystem())
        {
            storage = (await store.GetExactAsync<StorageResource>(new(ResourceScopeRef.Instance, ResourceNamespace.Default,
                StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))!.Value;
            if (owner != ResourceScopeRef.Instance)
            {
                // The fixture explicitly grants builtin type use; production grants remain unchanged.
                var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
                var localType = (await store.GetExactAsync<StorageTypeResource>(new(ResourceScopeRef.Instance, ResourceNamespace.Default,
                    StorageResourceKinds.StorageType, storage.Definition.Type.Type.Name), default))!.Value;
                if (ownerKind == "tenant")
                {
                    // Tenant administration intentionally cannot read Instance resources. Seed a tenant-owned
                    // Core local adapter fixture instead of weakening this boundary to reach the Instance type.
                    using var core = factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("storage.local", "1.0.0"));
                    var tenantType = (await factory.Services.GetRequiredService<Agentstration.Storage.StorageTypeManagementService>()
                        .SynchronizeManagedAsync(localType with { Uid = Guid.Empty, ScopeRef = owner, Metadata = new() { Name = "local-fixture" } }, default)).Value;
                    storage = storage with
                    {
                        Definition = storage.Definition with
                        {
                            Type = storage.Definition.Type with
                            { Type = new(tenantType.Name, owner, tenantType.Namespace), TypeUid = tenantType.Uid }
                        }
                    };
                }
                else
                    await policies.PutAsync(new(new(localType.Uid, localType.ScopeRef!.Value, localType.Namespace, localType.Kind),
                        [new(owner, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
            }
        }
        if (owner != ResourceScopeRef.Instance)
        {
            using var createStorage = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("openapi-datasets", storage.Definition, owner));
            Assert.AreEqual(HttpStatusCode.Created, createStorage.StatusCode, await createStorage.Content.ReadAsStringAsync());
            storage = (await createStorage.Content.ReadFromJsonAsync<StorageResource>())!;
        }
        var storageReference = new StorageRevisionReference(new(storage.ScopeRef!.Value, storage.Namespace, storage.Kind, storage.Name),
            storage.Uid, storage.Generation, storage.ObservedState!.BindingDigest!);
        var operationReference = new ConnectedServiceOperationRevisionReference(new(owner, selected.Namespace, selected.Kind, selected.Name),
            selected.Uid, selected.Generation, selected.Definition.DefinitionDigest);
        using (var invalidSave = await client.PostAsJsonAsync("/api/datasources", new CreateDataSourceRequest("missing-parameters",
            new() { DisplayName = "Invalid", Dataset = new(storageReference, new(Write: true), new(operationReference, JsonSerializer.SerializeToElement(new { }))) }, ScopeRef: owner)))
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidSave.StatusCode);
            var problem = await invalidSave.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("https://agentstration.dev/problems/data_source_parameters_invalid", problem.GetProperty("type").GetString());
        }
        var results = new List<DataSourceAcquisitionResource>();
        foreach (var dataset in new[] { "alpha", "beta" })
        {
            var parameters = JsonSerializer.SerializeToElement(new { query = new { dataset } });
            var definition = new DataSourceDatasetProperties(storageReference, new(Write: true), new(operationReference, parameters));
            // Exercise shared Instance functions where this fixture can read Instance resources.
            // Tenant administration intentionally keeps its isolated acquisition-only fixture.
            var transform = dataset == "beta" && ownerKind != "tenant";
            if (transform) definition = definition with
            {
                Compositions = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>
                {
                    ["transformations"] = [new() { Function = new("json-select-builtin", "1.0.0", ResourceScopeRef.Instance),
                        Parameters = JsonSerializer.SerializeToElement(new { path = "/dataset" }) }]
                }
            };
            using var save = await client.PostAsJsonAsync("/api/datasources", new CreateDataSourceRequest(dataset,
                new() { DisplayName = dataset, Dataset = definition }, ScopeRef: owner));
            Assert.AreEqual(HttpStatusCode.Created, save.StatusCode, await save.Content.ReadAsStringAsync());
            using var accepted = await client.PostAsJsonAsync($"/api/datasources/{dataset}/acquisitions?scopeRef={Uri.EscapeDataString(owner.Value)}", new StartDataSourceAcquisitionRequest());
            Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
            var acquisition = (await accepted.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>())!;
            using var acquisitionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            do
            {
                await Task.Delay(25, acquisitionTimeout.Token);
                acquisition = (await client.GetFromJsonAsync<DataSourceAcquisitionResource>(
                    $"/api/datasourceacquisitions/{acquisition.Name}", acquisitionTimeout.Token))!;
            } while (acquisition.CompletedAt is null);
            Assert.AreEqual(DataSourceAcquisitionState.Succeeded, acquisition.State, $"{acquisition.ErrorCode}: {acquisition.ErrorMessage}");
            if (transform)
            {
                Assert.AreEqual("json-select-builtin", acquisition.Dataset!.Composition!.Receipt!.Bindings["transformations"].Single().Function!.Name);
                Assert.AreEqual("object", acquisition.Dataset.OperationOutputSchema!.Value.GetProperty("type").GetString());
            }
            using var read = await client.GetAsync($"/api/datasourceacquisitions/{acquisition.Name}/content");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var content = (await read.Content.ReadFromJsonAsync<StorageObjectContent>())!;
            var storedJson = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(content.ContentBase64));
            Assert.AreEqual(dataset, transform ? storedJson.GetString() : storedJson.GetProperty("dataset").GetString());
            results.Add(acquisition);
        }
        Assert.AreNotEqual(results[0].Content!.Key, results[1].Content!.Key);
        Assert.AreEqual(5, calls); // discovery + contract and business request per acquisition
        Assert.AreEqual(baseBinding == "literal-no-secret" ? 0 : 3, secretCallbacks);
        using (ownerKind switch
        {
            "instance" => scopes.PushInstance(context),
            "tenant" => scopes.PushTenantAdministration(context),
            _ => scopes.Push(context)
        })
        {
            var executor = factory.Services.GetRequiredService<ConnectedServiceAcquisitionExecutor>();
            var invocation = new InternalMcpToolInvocation(context.TenantId, new(context.WorkspaceId), context.PrincipalId,
                "forged-acquisition-call", "test", JsonSerializer.SerializeToElement(new { acquisitionId = results[0].Name }),
                ToolDefinitionCallerKind.Flow, RunId: results[0].FlowRunId);
            foreach (var invalid in new[]
            {
                invocation with { WorkspaceId = new(Guid.NewGuid()) }, invocation with { TenantId = Guid.NewGuid() },
                invocation with { PrincipalId = Guid.NewGuid() }, invocation with { CallerKind = ToolDefinitionCallerKind.Agent },
                invocation with { RunId = null }
            })
            {
                var denied = await Assert.ThrowsAsync<Agentstration.DataSources.DataSourceAcquisitionException>(() => executor.ExecuteAsync(invalid, default));
                Assert.AreEqual("connected_service_acquisition_execution_denied", denied.Code);
            }
            // An arbitrary run, a completed acquisition, or another logical context cannot dispatch the provider.
            await Assert.ThrowsAsync<Agentstration.DataSources.DataSourceAcquisitionException>(() => executor.ExecuteAsync(invocation with { RunId = "another-run" }, default));
            await Assert.ThrowsAsync<Agentstration.DataSources.DataSourceAcquisitionException>(() => executor.ExecuteAsync(invocation, default));
            using (ownerKind == "workspace" ? scopes.PushInstance(context) : scopes.Push(context))
                await Assert.ThrowsAsync<Agentstration.DataSources.DataSourceAcquisitionException>(() => executor.ExecuteAsync(invocation, default));
        }
        Assert.AreEqual(5, calls);
        var runScope = new FlowRunScope(context.TenantId, new(context.WorkspaceId), context.PrincipalId)
        { LogicalScopeRef = owner };
        var runs = factory.Services.GetRequiredService<FlowRunService>();
        var run = (await runs.GetAsync(discovery.FlowRunId, runScope, default))!.Value;
        var events = await runs.ListEventsAsync(runScope, discovery.FlowRunId, 0, default);
        var acquisitionEvidence = new List<object>();
        foreach (var acquired in results)
        {
            var acquisitionRun = (await runs.GetAsync(acquired.FlowRunId, runScope, default))!.Value;
            var acquisitionEvents = await runs.ListEventsAsync(runScope, acquired.FlowRunId, 0, default);
            acquisitionEvidence.Add(new { acquisitionRun, acquisitionEvents });
            Assert.DoesNotContain("https://origin.example.test", acquisitionRun.Input.GetRawText(), StringComparison.Ordinal);
        }
        var evidence = JsonSerializer.Serialize(new { discovery, operations, run, events, acquisitions = results, acquisitionEvidence });
        Assert.DoesNotContain(credential, evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("https://origin.example.test", run.Input.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("secretGrant", evidence, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SecretCallbackHandler(HttpMessageHandler inner, Action called) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            called();
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class DiscoveryRequestHandler(HttpMessageHandler inner, Action called) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == AepConnectedServiceDiscoveryProtocol.Path) called();
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class PublicAddressResolver : OpenApi.IOpenApiAddressResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("8.8.8.8")]);
    }

    private sealed class ContractHandler(Action called, string? token) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(token is null ? null : "Bearer " + token, request.Headers.Authorization?.ToString());
            called();
            if (request.RequestUri!.AbsolutePath == "/orders")
            {
                var dataset = request.RequestUri.Query.Contains("alpha", StringComparison.Ordinal) ? "alpha" : "beta";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { dataset }) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"openapi":"3.1.0","info":{"title":"Orders","version":"1"},"paths":{
                      "/orders":{"get":{"operationId":"getOrders","parameters":[{"in":"query","name":"dataset","required":true,"schema":{"type":"string"}}],"responses":{"200":{"description":"OK","content":{"application/json; charset=utf-8":{"schema":{"type":"object","properties":{"dataset":{"type":"string"}},"required":["dataset"],"additionalProperties":false}}}}}}}
                    }}
                    """, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
