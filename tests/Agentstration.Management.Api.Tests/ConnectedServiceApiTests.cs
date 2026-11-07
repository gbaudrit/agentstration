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
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ConnectedServiceApiTests : ModelManagementApiTestBase
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
        var version = type.Definition.Versions.Single();
        Assert.AreEqual("1.0.0", version.Version);
        Assert.AreEqual("baseUrl", version.Inputs.Single().Id);
        Assert.AreEqual("baseUrl", version.Exports.Single().SourceInputId);
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
}
