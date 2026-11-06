using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ConnectedServiceApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task CatalogCrudAndResolutionUsePinnedTypeAndLateBoundParameter()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConnectedServiceTypeProvider>();
            services.AddSingleton<IConnectedServiceTypeProvider, TestConnectedServiceTypeProvider>();
        }));
        using var client = factory.CreateClient();
        var tenant = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ConnectedServiceResourceKinds.ConnectedService}"))!
            .Single(value => value.Kind == ResourceScopeKind.Tenant).ScopeRef;
        SelectConsoleContext(client, tenant);

        using var extensionResponse = await client.PostAsJsonAsync("/api/extensionregistrations",
            new CreateExtensionRegistrationRequest(
                "cloud-extension",
                new ExtensionRegistrationProperties
                {
                    DisplayName = "Cloud extension",
                    Endpoint = new("http://127.0.0.1:6788"),
                    ExpectedExtensionId = TestConnectedServiceTypeProvider.ExtensionId
                },
                ScopeRef: tenant));
        Assert.AreEqual(HttpStatusCode.Created, extensionResponse.StatusCode);
        Assert.IsNotNull(extensionResponse.Headers.ETag);

        using var parameterResponse = await client.PostAsJsonAsync("/api/parameters",
            new CreateParameterRequest("storage-endpoint", new ParameterProperties
            {
                DisplayName = "Storage endpoint",
                ValueType = ParameterValueType.Text,
                Value = JsonSerializer.SerializeToElement("https://storage.example.test")
            }, tenant));
        Assert.AreEqual(HttpStatusCode.Created, parameterResponse.StatusCode);

        var catalog = await client.GetFromJsonAsync<ConnectedServiceTypeCatalogEntry[]>(
            "/api/connected-service-types");
        var type = catalog!.Single();
        var version = type.Type.Versions.Single();
        Assert.AreEqual("object-storage", type.Type.Id);
        Assert.AreEqual(TestConnectedServiceTypeProvider.ExtensionId, type.ExtensionIdentity.Id);

        var properties = new ConnectedServiceProperties
        {
            DisplayName = "S3 account",
            Type = new ConnectedServiceTypeReference
            {
                Extension = new ResourceReference("cloud-extension", tenant),
                ExtensionId = type.ExtensionIdentity.Id,
                TypeId = type.Type.Id,
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
        Assert.AreEqual(ConnectedServiceAvailability.Ready, created!.ObservedState?.Availability);
        Assert.IsFalse((await createdResponse.Content.ReadAsStringAsync()).Contains(
            "https://storage.example.test", StringComparison.Ordinal));

        using var deleteParameter = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/parameters/storage-endpoint?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        deleteParameter.Headers.IfMatch.Add(parameterResponse.Headers.ETag!);
        using var parameterInUse = await client.SendAsync(deleteParameter);
        Assert.AreEqual(HttpStatusCode.Conflict, parameterInUse.StatusCode);

        using var deleteExtension = new HttpRequestMessage(HttpMethod.Delete,
            "/api/extensionregistrations/cloud-extension");
        deleteExtension.Headers.IfMatch.Add(extensionResponse.Headers.ETag!);
        using var extensionInUse = await client.SendAsync(deleteExtension);
        Assert.AreEqual(HttpStatusCode.Conflict, extensionInUse.StatusCode);

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

        ((TestConnectedServiceTypeProvider)factory.Services.GetRequiredService<IConnectedServiceTypeProvider>())
            .PublishType = false;
        using var reconcile = new HttpRequestMessage(HttpMethod.Post,
            $"/api/connected-services/{created.Name}/reconcile?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        reconcile.Headers.IfMatch.Add(createdResponse.Headers.ETag!);
        using var reconciledResponse = await client.SendAsync(reconcile);
        Assert.AreEqual(HttpStatusCode.OK, reconciledResponse.StatusCode);
        Assert.AreEqual(ConnectedServiceAvailability.Stale,
            (await reconciledResponse.Content.ReadFromJsonAsync<ConnectedServiceResource>())!.ObservedState?.Availability);

        using var deactivate = new HttpRequestMessage(HttpMethod.Post,
            $"/api/connected-services/{created.Name}/deactivate?scopeRef={Uri.EscapeDataString(tenant.Value)}");
        deactivate.Headers.IfMatch.Add(reconciledResponse.Headers.ETag!);
        using var deactivated = await client.SendAsync(deactivate);
        Assert.AreEqual(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.AreEqual(ConnectedServiceAvailability.Disabled,
            (await deactivated.Content.ReadFromJsonAsync<ConnectedServiceResource>())!.ObservedState?.Availability);
    }

    [TestMethod]
    public async Task CreateRejectsUnpinnedOrUnknownInputContracts()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConnectedServiceTypeProvider>();
            services.AddSingleton<IConnectedServiceTypeProvider, TestConnectedServiceTypeProvider>();
        }));
        using var client = factory.CreateClient();
        var tenant = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>(
            $"/api/resource-scopes/targets?kind={ConnectedServiceResourceKinds.ConnectedService}"))!
            .Single(value => value.Kind == ResourceScopeKind.Tenant).ScopeRef;
        SelectConsoleContext(client, tenant);
        using var extension = await client.PostAsJsonAsync("/api/extensionregistrations",
            new CreateExtensionRegistrationRequest("cloud-extension", new()
            {
                DisplayName = "Cloud extension",
                Endpoint = new("http://127.0.0.1:6788")
            }, ScopeRef: tenant));
        Assert.AreEqual(HttpStatusCode.Created, extension.StatusCode);

        using var response = await client.PostAsJsonAsync("/api/connected-services",
            new CreateConnectedServiceRequest("invalid", new ConnectedServiceProperties
            {
                DisplayName = "Invalid",
                Type = new ConnectedServiceTypeReference
                {
                    Extension = new ResourceReference("cloud-extension", tenant),
                    ExtensionId = TestConnectedServiceTypeProvider.ExtensionId,
                    TypeId = "object-storage",
                    Version = "1.0",
                    SchemaDigest = "sha256:wrong"
                },
                Options = JsonSerializer.SerializeToElement(new { region = "eu-west-1" }),
                Bindings = []
            }, tenant));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private sealed class TestConnectedServiceTypeProvider : IConnectedServiceTypeProvider
    {
        public const string ExtensionId = "io.agentstration.test.cloud";
        private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            required = new[] { "region" },
            properties = new { region = new { type = "string" } },
            additionalProperties = false
        });
        public bool PublishType { get; set; } = true;

        public bool CanInspect(Uri endpoint) => endpoint.Port == 6788;

        public ValueTask<ConnectedServiceTypeCatalogInspection> InspectAsync(
            ExtensionRegistrationResource registration,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new ConnectedServiceTypeCatalogInspection(
            "available",
            new(ExtensionId, "Test cloud", "1.0.0"),
            PublishType ?
            [
                new ConnectedServiceTypeDescriptor(
                    "object-storage",
                    "Object storage account",
                    "1.0",
                    [new("1.0", Digest(Schema), Schema, [new("endpoint", true,
                        ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Standard)],
                        [new("connection", ConnectedServiceValueType.Text,
                            ConnectedServiceValueProtection.Secured)])])
            ] : []));

        private static string Digest(JsonElement schema) => Agentstration.Aep.Abstractions.AepSchemaDigest.Compute(schema);
    }
}
