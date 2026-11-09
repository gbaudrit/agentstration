extern alias openapi;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Tools;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Contracts;
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
        var runScope = new FlowRunScope(context.TenantId, new(context.WorkspaceId), context.PrincipalId)
        { LogicalScopeRef = owner };
        var runs = factory.Services.GetRequiredService<FlowRunService>();
        var run = (await runs.GetAsync(discovery.FlowRunId, runScope, default))!.Value;
        var events = await runs.ListEventsAsync(runScope, discovery.FlowRunId, 0, default);
        var evidence = JsonSerializer.Serialize(new { discovery, operations, run, events });
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"openapi":"3.1.0","info":{"title":"Orders","version":"1"},"paths":{
                      "/orders":{"get":{"operationId":"getOrders","responses":{"200":{"description":"OK"}}}}
                    }}
                    """, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
