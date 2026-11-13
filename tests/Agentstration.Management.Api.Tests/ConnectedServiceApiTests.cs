using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed partial class ConnectedServiceApiTests : ModelManagementApiTestBase
{
    private static readonly JsonElement ObjectStorageSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "region" },
        properties = new { region = new { type = "string" } },
        additionalProperties = false
    });

    [TestMethod]
    public async Task TypeResourceCrudAndResolutionUsePinnedRevisionAndLateBoundParameter()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var tenant = await TenantScopeAsync(client);
        SelectConsoleContext(client, tenant);

        var invalidVersion = TypeProperties().Versions.Single() with { SchemaDigest = "sha256:wrong" };
        using var invalidType = await client.PostAsJsonAsync("/api/connected-service-types",
            new CreateConnectedServiceTypeRequest("invalid-storage",
                TypeProperties() with { Versions = [invalidVersion] }, tenant));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidType.StatusCode);

        using var typeResponse = await client.PostAsJsonAsync("/api/connected-service-types",
            new CreateConnectedServiceTypeRequest("object-storage", TypeProperties(), tenant));
        Assert.AreEqual(HttpStatusCode.Created, typeResponse.StatusCode);
        Assert.IsNotNull(typeResponse.Headers.ETag);
        var type = await typeResponse.Content.ReadFromJsonAsync<ConnectedServiceTypeResource>();
        Assert.IsNotNull(type);
        Assert.AreEqual(ResourceOriginKind.User, type.Governance.Origin.Kind);

        using var parameterResponse = await client.PostAsJsonAsync("/api/parameters",
            new CreateParameterRequest("storage-endpoint", new ParameterProperties
            {
                DisplayName = "Storage endpoint",
                ValueType = ParameterValueType.Text,
                Value = JsonSerializer.SerializeToElement("https://storage.example.test")
            }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, parameterResponse.StatusCode);

        var version = type.Definition.Versions.Single();
        var properties = new ConnectedServiceProperties
        {
            DisplayName = "S3 account",
            Type = new ConnectedServiceTypeReference
            {
                Type = new ResourceReference(type.Name, tenant, type.Namespace),
                TypeUid = type.Uid,
                Version = version.Version,
                SchemaDigest = version.SchemaDigest
            },
            Options = JsonSerializer.SerializeToElement(new { region = "eu-west-1" }),
            Bindings =
            [
                new ConnectedServiceValueBinding
                {
                    InputId = "endpoint",
                    Kind = ConnectedServiceBindingKind.Parameter,
                    Parameter = new ParameterReference(
                        ResourceAddress.Create(ResourceNamespace.Default, ParameterResourceKinds.Parameter,
                            "storage-endpoint"), tenant)
                }
            ]
        };
        using var createdResponse = await client.PostAsJsonAsync("/api/connected-services",
            new CreateConnectedServiceRequest("s3-account", properties, tenant));
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<ConnectedServiceResource>();
        Assert.IsNotNull(created);
        Assert.AreEqual(ConnectedServiceAvailability.Ready, created.ObservedState?.Availability);
        Assert.IsFalse((await createdResponse.Content.ReadAsStringAsync()).Contains(
            "https://storage.example.test", StringComparison.Ordinal));
        var usages = await client.GetFromJsonAsync<ConnectedServiceUsagesResponse>(
            $"/api/connected-services/{created.Name}/usages?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        Assert.IsNotNull(usages);
        Assert.AreEqual(0, usages.Count);

        using var deleteParameter = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/parameters/storage-endpoint?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        deleteParameter.Headers.IfMatch.Add(parameterResponse.Headers.ETag!);
        using var parameterInUse = await client.SendAsync(deleteParameter);
        Assert.AreEqual(HttpStatusCode.Conflict, parameterInUse.StatusCode);

        using var deleteType = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/connected-service-types/{type.Name}?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        deleteType.Headers.IfMatch.Add(typeResponse.Headers.ETag!);
        using var typeInUse = await client.SendAsync(deleteType);
        Assert.AreEqual(HttpStatusCode.Conflict, typeInUse.StatusCode);

        var resolver = factory.Services.GetRequiredService<IConnectedServiceResolver>();
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        using (var resolved = await resolver.ResolveAsync(
            new ConnectedServiceReference(created.Address, tenant),
            new ConnectedServiceResolutionContext(tenant,
                ResourceAddress.Create(ResourceNamespace.Default, "Storage", "consumer"))))
        {
            Assert.IsNotNull(resolved);
            Assert.AreEqual("https://storage.example.test", resolved.Inputs.Single().Value?.GetString());
        }

        using var disableType = new HttpRequestMessage(HttpMethod.Put,
            $"/api/connected-service-types/{type.Name}?scopeRef={Uri.EscapeDataString(tenant.Value)}")
        {
            Content = JsonContent.Create(new PutConnectedServiceTypeRequest(
                type.Definition with { Enabled = false }))
        };
        disableType.Headers.IfMatch.Add(typeResponse.Headers.ETag!);
        using var disabledType = await client.SendAsync(disableType);
        Assert.AreEqual(HttpStatusCode.OK, disabledType.StatusCode);

        using var reconcile = new HttpRequestMessage(HttpMethod.Post,
            $"/api/connected-services/{created.Name}/reconcile?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        reconcile.Headers.IfMatch.Add(createdResponse.Headers.ETag!);
        using var reconciledResponse = await client.SendAsync(reconcile);
        Assert.AreEqual(HttpStatusCode.OK, reconciledResponse.StatusCode);
        Assert.AreEqual(ConnectedServiceAvailability.Unavailable,
            (await reconciledResponse.Content.ReadFromJsonAsync<ConnectedServiceResource>())!.ObservedState?.Availability);
    }

    [TestMethod]
    public async Task BuiltInHttpTypeIsACoreOwnedResource()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<IWorkspacePlatformResourceProvisioner>()
            .EnsureAllAsync(CancellationToken.None);
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var stored = await factory.Services.GetRequiredService<ConnectedServiceTypeManagementService>()
                .GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default,
                    ConnectedServicePlatformResourceProvisioner.HttpTypeName, default);
            Assert.IsNotNull(stored);
        }

        var catalog = await client.GetFromJsonAsync<ConnectedServiceTypeResource[]>(
            "/api/connected-service-types");

        var type = catalog!.Single(value => value.Name == "http-endpoint");
        Assert.AreEqual(ResourceScopeRef.Instance, type.ScopeRef);
        Assert.AreEqual(ResourceOriginKind.BuiltIn, type.Governance.Origin.Kind);
        Assert.AreEqual(ResourceManagementAuthorityKind.Core, type.Governance.ManagedBy?.Kind);
        Assert.AreEqual("connected-services.http", type.Governance.ManagedBy?.Reference);
        Assert.HasCount(2, type.Definition.Versions);
        Assert.IsNull(type.Definition.Versions.Single(value => value.Version == "1.0.0").OperationDiscovery);
        var version = type.Definition.Versions.Single(value => value.Version == type.Definition.PreferredVersion);
        Assert.AreEqual("1.1.0", version.Version);
        Assert.IsNotNull(version.OperationDiscovery);
        Assert.AreEqual("baseUrl", version.Inputs.Single().Id);
        Assert.AreEqual("baseUrl", version.Exports.Single().SourceInputId);
    }

    [TestMethod]
    public async Task LiteralBindingsPersistResolveAndRemainUnavailableForSecuredInputs()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var tenant = await TenantScopeAsync(client);
        SelectConsoleContext(client, tenant);

        using var typeResponse = await client.PostAsJsonAsync("/api/connected-service-types",
            new CreateConnectedServiceTypeRequest("literal-storage", TypeProperties(), tenant));
        Assert.AreEqual(HttpStatusCode.Created, typeResponse.StatusCode);
        var type = (await typeResponse.Content.ReadFromJsonAsync<ConnectedServiceTypeResource>())!;
        var version = type.Definition.Versions.Single();
        var properties = new ConnectedServiceProperties
        {
            DisplayName = "Literal storage",
            Type = new ConnectedServiceTypeReference
            {
                Type = new ResourceReference(type.Name, tenant, type.Namespace),
                TypeUid = type.Uid,
                Version = version.Version,
                SchemaDigest = version.SchemaDigest
            },
            Options = JsonSerializer.SerializeToElement(new { region = "eu-west-1" }),
            Bindings =
            [
                new ConnectedServiceValueBinding
                {
                    InputId = "endpoint",
                    Kind = ConnectedServiceBindingKind.Literal,
                    Literal = JsonSerializer.SerializeToElement("https://literal.example.test")
                }
            ]
        };

        using var createdResponse = await client.PostAsJsonAsync("/api/connected-services",
            new CreateConnectedServiceRequest("literal-storage", properties, tenant));
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<ConnectedServiceResource>())!;
        Assert.AreEqual("https://literal.example.test",
            created.Definition.Bindings.Single().Literal?.GetString());

        var resolver = factory.Services.GetRequiredService<IConnectedServiceResolver>();
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        using (var resolved = await resolver.ResolveAsync(
            new ConnectedServiceReference(created.Address, tenant),
            new ConnectedServiceResolutionContext(tenant,
                ResourceAddress.Create(ResourceNamespace.Default, "Storage", "consumer"))))
        {
            Assert.IsNotNull(resolved);
            Assert.AreEqual("https://literal.example.test", resolved.Inputs.Single().Value?.GetString());
            Assert.IsNull(resolved.Inputs.Single().Secret);
        }

        using var wrongType = await client.PostAsJsonAsync("/api/connected-services",
            new CreateConnectedServiceRequest("wrong-literal", properties with
            {
                DisplayName = "Wrong literal",
                Bindings =
                [
                    new ConnectedServiceValueBinding
                    {
                        InputId = "endpoint",
                        Kind = ConnectedServiceBindingKind.Literal,
                        Literal = JsonSerializer.SerializeToElement(42)
                    }
                ]
            }, tenant));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, wrongType.StatusCode);

        var securedProperties = TypeProperties();
        var securedVersion = securedProperties.Versions.Single() with
        {
            Inputs = [new("endpoint", true, ConnectedServiceValueType.Text,
                ConnectedServiceValueProtection.Secured, "uri")]
        };
        using var securedTypeResponse = await client.PostAsJsonAsync("/api/connected-service-types",
            new CreateConnectedServiceTypeRequest("secured-storage",
                securedProperties with { Versions = [securedVersion] }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, securedTypeResponse.StatusCode);
        var securedType = (await securedTypeResponse.Content.ReadFromJsonAsync<ConnectedServiceTypeResource>())!;
        using var securedLiteral = await client.PostAsJsonAsync("/api/connected-services",
            new CreateConnectedServiceRequest("secured-literal", properties with
            {
                DisplayName = "Secured literal",
                Type = new ConnectedServiceTypeReference
                {
                    Type = new ResourceReference(securedType.Name, tenant, securedType.Namespace),
                    TypeUid = securedType.Uid,
                    Version = securedVersion.Version,
                    SchemaDigest = securedVersion.SchemaDigest
                }
            }, tenant));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, securedLiteral.StatusCode);
    }

    [TestMethod]
    public async Task TypeVersionsAreImmutableAndServiceCreationRejectsWrongDigest()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var tenant = await TenantScopeAsync(client);
        SelectConsoleContext(client, tenant);

        using var typeResponse = await client.PostAsJsonAsync("/api/connected-service-types",
            new CreateConnectedServiceTypeRequest("object-storage", TypeProperties(), tenant));
        Assert.AreEqual(HttpStatusCode.Created, typeResponse.StatusCode);
        var type = (await typeResponse.Content.ReadFromJsonAsync<ConnectedServiceTypeResource>())!;

        var changedSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = true
        });
        var changedVersion = type.Definition.Versions.Single() with
        {
            Schema = changedSchema,
            SchemaDigest = AepSchemaDigest.Compute(changedSchema)
        };
        using var mutateVersion = new HttpRequestMessage(HttpMethod.Put,
            $"/api/connected-service-types/{type.Name}?scopeRef={Uri.EscapeDataString(tenant.Value)}")
        {
            Content = JsonContent.Create(new PutConnectedServiceTypeRequest(
                type.Definition with { Versions = [changedVersion] }))
        };
        mutateVersion.Headers.IfMatch.Add(typeResponse.Headers.ETag!);
        using var mutationResponse = await client.SendAsync(mutateVersion);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, mutationResponse.StatusCode);

        using var invalidService = await client.PostAsJsonAsync("/api/connected-services",
            new CreateConnectedServiceRequest("invalid", new ConnectedServiceProperties
            {
                DisplayName = "Invalid",
                Type = new ConnectedServiceTypeReference
                {
                    Type = new ResourceReference(type.Name, tenant, type.Namespace),
                    TypeUid = type.Uid,
                    Version = "1.0.0",
                    SchemaDigest = "sha256:wrong"
                },
                Options = JsonSerializer.SerializeToElement(new { region = "eu-west-1" }),
                Bindings = []
            }, tenant));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidService.StatusCode);
    }

    [TestMethod]
    public async Task BootstrapMaterializesTypeAtTheProfileTargetScope()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var tenant = await TenantScopeAsync(client);
        await using var serviceScope = factory.Services.CreateAsyncScope();
        var handler = serviceScope.ServiceProvider.GetServices<IBootstrapResourceHandler>()
            .Single(value => value.Kind == ConnectedServiceResourceKinds.ConnectedServiceType);
        var document = new BootstrapResourceDocument
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            Metadata = new() { Name = "bootstrap-service-type" },
            Definition = JsonSerializer.SerializeToElement(TypeProperties())
        };
        var operation = new BootstrapResourceOperationContext(
            "tenant-resources",
            "tenant-resources",
            BootstrapProfileScope.Tenant,
            new(TenantId: tenant.TargetId));

        using var systemScope = serviceScope.ServiceProvider
            .GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var preview = await handler.PlanAsync(document, operation, new(), default);
        Assert.AreEqual(BootstrapResourceDisposition.Create, preview.Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Created,
            await handler.ApplyAsync(document, operation, default));
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped,
            await handler.ApplyAsync(document, operation, default));
        Assert.IsNotNull(await serviceScope.ServiceProvider
            .GetRequiredService<ConnectedServiceTypeManagementService>()
            .GetExactAsync(tenant, ResourceNamespace.Default, "bootstrap-service-type", default));
    }

    [TestMethod]
    public async Task McpShapedDiscoveryMaterializesTypedReadOnlyOperations()
    {
        var gateway = new DeterministicConnectedServiceDiscoveryGateway();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConnectedServiceOperationDiscoveryFlowGateway>();
            services.AddSingleton<IConnectedServiceOperationDiscoveryFlowGateway>(gateway);
        }));
        using var client = factory.CreateClient();
        var tenant = await WorkspaceScopeAsync(client);
        SelectConsoleContext(client, tenant);

        var operationSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            required = new[] { "name", "inputSchema" },
            properties = new
            {
                name = new { type = "string" },
                inputSchema = new { type = "object" }
            },
            additionalProperties = false
        });
        var operationTypeProperties = new ConnectedServiceOperationTypeProperties
        {
            DisplayName = "MCP tool",
            PreferredVersion = "1.0.0",
            Versions =
            [
                new("1.0.0", AepSchemaDigest.Compute(operationSchema), operationSchema,
                    ConnectedServiceOperationInteraction.RequestResponse,
                    new("mcp.tools/call", "1.0"))
            ]
        };
        using var operationTypeResponse = await client.PostAsJsonAsync(
            "/api/connected-service-operation-types",
            new CreateConnectedServiceOperationTypeRequest("mcp-tool", operationTypeProperties, tenant));
        Assert.AreEqual(HttpStatusCode.Created, operationTypeResponse.StatusCode);
        var operationType = (await operationTypeResponse.Content
            .ReadFromJsonAsync<ConnectedServiceOperationTypeResource>())!;
        var operationTypeReference = new ConnectedServiceOperationTypeReference
        {
            Type = new(operationType.Name, tenant, operationType.Namespace),
            TypeUid = operationType.Uid,
            Version = "1.0.0",
            SchemaDigest = operationType.Definition.Versions.Single().SchemaDigest
        };

        var serviceSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false
        });
        var serviceVersion = new ConnectedServiceTypeVersionDescriptor(
            "1.0.0", AepSchemaDigest.Compute(serviceSchema), serviceSchema, [], [])
        {
            OperationDiscovery = new()
            {
                Flow = new()
                {
                    Flow = new("mcp-discovery", tenant),
                    FlowUid = Guid.NewGuid(),
                    Version = "1.0.0",
                    DefinitionHash = "sha256:test-flow",
                    Contract = ConnectedServiceOperationDiscoveryContracts.V1
                },
                ProducedTypes = [operationTypeReference]
            }
        };
        using var serviceTypeResponse = await client.PostAsJsonAsync("/api/connected-service-types",
            new CreateConnectedServiceTypeRequest("mcp-server", new()
            {
                DisplayName = "MCP server",
                PreferredVersion = "1.0.0",
                Versions = [serviceVersion]
            }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, serviceTypeResponse.StatusCode);
        var serviceType = (await serviceTypeResponse.Content.ReadFromJsonAsync<ConnectedServiceTypeResource>())!;

        using var serviceResponse = await client.PostAsJsonAsync("/api/connected-services",
            new CreateConnectedServiceRequest("weather-mcp", new()
            {
                DisplayName = "Weather MCP",
                Type = new()
                {
                    Type = new(serviceType.Name, tenant, serviceType.Namespace),
                    TypeUid = serviceType.Uid,
                    Version = serviceVersion.Version,
                    SchemaDigest = serviceVersion.SchemaDigest
                },
                Options = JsonSerializer.SerializeToElement(new { })
            }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, serviceResponse.StatusCode);

        gateway.Output = JsonSerializer.SerializeToElement(new ConnectedServiceOperationDiscoveryManifest
        {
            Operations =
            [
                new()
                {
                    ExternalId = "tools/weather.current",
                    DisplayName = "Current weather",
                    Type = operationTypeReference,
                    Definition = JsonSerializer.SerializeToElement(new
                    {
                        name = "weather.current",
                        inputSchema = new { type = "object" }
                    })
                }
            ]
        });
        using var start = await client.PostAsJsonAsync(
            $"/api/connected-services/weather-mcp/operation-discoveries?scopeRef={Uri.EscapeDataString(tenant.Value)}",
            new StartConnectedServiceOperationDiscoveryRequest(IdempotencyKey: "mcp-fixture"));
        Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode,
            await start.Content.ReadAsStringAsync());
        var discovery = (await start.Content
            .ReadFromJsonAsync<ConnectedServiceOperationDiscoveryResource>())!;
        using var synchronized = await client.GetAsync(
            $"/api/connected-service-operation-discoveries/{discovery.Name}");
        Assert.AreEqual(HttpStatusCode.OK, synchronized.StatusCode);

        using var operationsResponse = await client.GetAsync(
            $"/api/connected-services/weather-mcp/operations?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        Assert.AreEqual(HttpStatusCode.OK, operationsResponse.StatusCode);
        var operations = (await operationsResponse.Content
            .ReadFromJsonAsync<ConnectedServiceOperationResource[]>())!;
        Assert.HasCount(1, operations);
        Assert.AreEqual("tools/weather.current", operations[0].Definition.ExternalId);
        Assert.AreEqual(ConnectedServiceOperationAvailability.Available,
            operations[0].ObservedState.Availability);
        Assert.AreEqual("mcp.tools/call", operationType.Definition.Versions.Single().Invocation.ProviderOperationId);

        gateway.Output = JsonSerializer.SerializeToElement(new ConnectedServiceOperationDiscoveryManifest());
        using var secondStart = await client.PostAsJsonAsync(
            $"/api/connected-services/weather-mcp/operation-discoveries?scopeRef={Uri.EscapeDataString(tenant.Value)}",
            new StartConnectedServiceOperationDiscoveryRequest(IdempotencyKey: "mcp-fixture-empty"));
        Assert.AreEqual(HttpStatusCode.Accepted, secondStart.StatusCode);
        var secondDiscovery = (await secondStart.Content
            .ReadFromJsonAsync<ConnectedServiceOperationDiscoveryResource>())!;
        using var secondSynchronized = await client.GetAsync(
            $"/api/connected-service-operation-discoveries/{secondDiscovery.Name}");
        Assert.AreEqual(HttpStatusCode.OK, secondSynchronized.StatusCode);
        var rediscovered = (await client.GetFromJsonAsync<ConnectedServiceOperationResource[]>(
            $"/api/connected-services/weather-mcp/operations?scopeRef={Uri.EscapeDataString(tenant.Value)}"))!;
        Assert.HasCount(1, rediscovered);
        Assert.AreEqual(operations[0].Uid, rediscovered[0].Uid);
        Assert.AreEqual(ConnectedServiceOperationAvailability.Unavailable,
            rediscovered[0].ObservedState.Availability);
    }

    private static ConnectedServiceTypeProperties TypeProperties() => new()
    {
        DisplayName = "Object storage account",
        PreferredVersion = "1.0.0",
        Versions =
        [
            new ConnectedServiceTypeVersionDescriptor(
                "1.0.0",
                AepSchemaDigest.Compute(ObjectStorageSchema),
                ObjectStorageSchema,
                [new("endpoint", true, ConnectedServiceValueType.Text,
                    ConnectedServiceValueProtection.Standard, "uri")],
                [new("connection", ConnectedServiceValueType.Text,
                    ConnectedServiceValueProtection.Secured)])
        ]
    };

    private static async Task<ResourceScopeRef> TenantScopeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ConnectedServiceResourceKinds.ConnectedService}"))!
        .Single(value => value.Kind == ResourceScopeKind.Tenant).ScopeRef;

    private static async Task<ResourceScopeRef> WorkspaceScopeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ConnectedServiceResourceKinds.ConnectedService}"))!
        .Single(value => value.Kind == ResourceScopeKind.Workspace).ScopeRef;

    private sealed class DeterministicConnectedServiceDiscoveryGateway
        : IConnectedServiceOperationDiscoveryFlowGateway
    {
        private ConnectedServiceDiscoveryFlowRunRequest? request;
        public JsonElement Output { get; set; }

        public Task<ConnectedServiceDiscoveryFlowRunSnapshot> StartAsync(
            ConnectedServiceDiscoveryFlowRunRequest value, CancellationToken cancellationToken)
        {
            request = value;
            return Task.FromResult(new ConnectedServiceDiscoveryFlowRunSnapshot(value.RunId,
                ConnectedServiceOperationDiscoveryState.Pending, null, null, null, null));
        }

        public Task<ConnectedServiceDiscoveryFlowRunSnapshot?> GetAsync(
            Guid workspaceId, string runId, Guid tenantId, Guid principalId,
            CancellationToken cancellationToken) => Task.FromResult<ConnectedServiceDiscoveryFlowRunSnapshot?>(
            request is null ? null : new(runId, ConnectedServiceOperationDiscoveryState.Succeeded,
                Output.Clone(), null, null, DateTimeOffset.UtcNow));

        public Task<ConnectedServiceDiscoveryFlowRunSnapshot> CancelAsync(
            Guid workspaceId, string runId, Guid tenantId, Guid principalId,
            CancellationToken cancellationToken) => Task.FromResult(new ConnectedServiceDiscoveryFlowRunSnapshot(
                runId, ConnectedServiceOperationDiscoveryState.Cancelled, null, null, null,
                DateTimeOffset.UtcNow));
    }
}
