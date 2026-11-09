using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceDatasetEditorTests
{
    [TestMethod]
    [DataRow("en-US", "No configuration")]
    [DataRow("fr-FR", "Sans configuration")]
    public async Task EmptyInputShowsNoArtificialFormAndCreationPinsReviewedOperationAndStorage(string cultureName, string expected)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext();
        using var handler = new DatasetHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://agentstration.test/") };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var state = new ConsoleContextState(new ContextProvider());
        await state.EnsureLoadedAsync(default);
        context.Services.AddSingleton(state);
        context.Services.AddSingleton<IDataSourcesClient>(new DataSourcesApiClient(http));
        context.Services.AddSingleton<IStoragesClient>(new StoragesApiClient(http));
        context.Services.AddSingleton<IConnectedServicesClient>(new ConnectedServicesApiClient(http));
        context.Services.AddSingleton<IConnectedServiceOperationsClient>(new ConnectedServiceOperationsApiClient(http));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(new ResourceAccessPolicyApiClient(http));
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ResourceScopeInventoryApiClient(http));
        var rendered = context.Render<DataSourceDatasetEditor>(parameters => parameters.Add(component => component.ScopeRef, "/instance"));
        rendered.WaitForElement("[data-testid='dataset-operation']");
        var storage = handler.Storage;
        var operation = handler.Operation;
        await rendered.Find("[data-testid='dataset-storage']").ChangeAsync($"{storage.Uid}:{storage.Generation}:{storage.ObservedState!.BindingDigest}");
        await rendered.Find("[data-testid='dataset-service']").ChangeAsync(handler.Service.Uid.ToString());
        await rendered.Find("[data-testid='dataset-operation']").ChangeAsync($"{operation.Uid}:{operation.Generation}:{operation.Definition.DefinitionDigest}");
        rendered.WaitForAssertion(() => Assert.Contains(expected, rendered.Find("[data-testid='dataset-parameters']").TextContent));
        Assert.HasCount(0, rendered.FindAll("[data-testid='dataset-parameters'] input, [data-testid='dataset-parameters'] textarea"));
        Assert.IsNull(handler.Created);
        await rendered.Find("[data-testid='data-source-display-name']").ChangeAsync("Inventory");
        await rendered.Find("[data-testid='data-source-name']").ChangeAsync("inventory");
        await rendered.Find("form").SubmitAsync();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Created));
        Assert.IsNull(handler.Created!.Properties.Profile);
        Assert.AreEqual(storage.Uid, handler.Created.Properties.Dataset!.Storage.StorageUid);
        Assert.AreEqual(operation.Uid, handler.Created.Properties.Dataset.Acquisition.Operation.OperationUid);
        Assert.AreEqual(operation.Generation, handler.Created.Properties.Dataset.Acquisition.Operation.Generation);
        Assert.AreEqual("{}", handler.Created.Properties.Dataset.Acquisition.Parameters.GetRawText());
        Assert.AreEqual("{}", handler.Created.Properties.Configuration.GetRawText());
        Assert.AreEqual(0, handler.Activations);
        Assert.AreEqual(0, handler.Discoveries);
        Assert.IsEmpty(rendered.FindAll("[data-testid='connected-service-start-discovery'], [data-testid='dataset-operation-enabled']"));
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='data-source-start-acquisition']").HasAttribute("disabled")));
    }

    [TestMethod]
    public async Task ServiceSelectionFiltersOperationsAndChangingParentClearsTheSavedCall()
    {
        using var handler = new DatasetHandler(withParameters: true);
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.ScopeRef, "/instance"));
        rendered.WaitForElement("[data-testid='dataset-service']");
        Assert.IsTrue(rendered.Find("[data-testid='dataset-operation']").HasAttribute("disabled"));
        Assert.IsEmpty(rendered.FindAll("[data-testid='dataset-parameters'] textarea"));
        await rendered.Find("[data-testid='dataset-service']").ChangeAsync(handler.Service.Uid.ToString());
        var operationKey = $"{handler.Operation.Uid}:{handler.Operation.Generation}:{handler.Operation.Definition.DefinitionDigest}";
        var otherKey = $"{handler.OtherOperation.Uid}:{handler.OtherOperation.Generation}:{handler.OtherOperation.Definition.DefinitionDigest}";
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll($"[data-testid='dataset-operation'] option[value='{operationKey}']"));
            Assert.IsEmpty(rendered.FindAll($"[data-testid='dataset-operation'] option[value='{otherKey}']"));
        });
        await rendered.Find("[data-testid='dataset-operation']").ChangeAsync(operationKey);
        await rendered.Find("[data-testid='dataset-parameters'] [data-schema-path='$.query'] input").InputAsync("first-service-query");
        await rendered.Find("[data-testid='dataset-service']").ChangeAsync(handler.OtherService.Uid.ToString());
        Assert.AreEqual("", rendered.Find("[data-testid='dataset-operation']").GetAttribute("value"));
        Assert.IsTrue(rendered.Find("[data-testid='data-source-save']").HasAttribute("disabled"));
        Assert.IsEmpty(rendered.FindAll($"[data-testid='dataset-operation'] option[value='{operationKey}']"));
        Assert.HasCount(1, rendered.FindAll($"[data-testid='dataset-operation'] option[value='{otherKey}']"));
        await rendered.Find("[data-testid='dataset-operation']").ChangeAsync(otherKey);
        Assert.AreEqual("", rendered.Find("[data-testid='dataset-parameters'] [data-schema-path='$.query'] input").GetAttribute("value"));
        Assert.IsNull(handler.Created);
        Assert.AreEqual(0, handler.Discoveries);
    }

    [TestMethod]
    public async Task EditingAnOlderOperationPinSelectsItsParentWithoutAdoptingTheNewRevision()
    {
        using var handler = new DatasetHandler { Existing = true, NewerOperation = true };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.Name, "inventory").Add(c => c.ScopeRef, "/instance"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(handler.Service.Uid.ToString(), rendered.Find("[data-testid='dataset-service']").GetAttribute("value"));
            Assert.AreEqual($"{handler.Operation.Uid}:{handler.Operation.Generation}:{handler.Operation.Definition.DefinitionDigest}", rendered.Find("[data-testid='dataset-operation']").GetAttribute("value"));
            Assert.IsTrue(rendered.Find("[data-testid='data-source-save']").HasAttribute("disabled"));
            Assert.IsNull(handler.Created);
        });
    }

    private static async Task<BunitContext> SetupAsync(HttpClient http)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var state = new ConsoleContextState(new ContextProvider());
        await state.EnsureLoadedAsync(default);
        context.Services.AddSingleton(state);
        context.Services.AddSingleton<IDataSourcesClient>(new DataSourcesApiClient(http));
        context.Services.AddSingleton<IStoragesClient>(new StoragesApiClient(http));
        context.Services.AddSingleton<IConnectedServicesClient>(new ConnectedServicesApiClient(http));
        context.Services.AddSingleton<IConnectedServiceOperationsClient>(new ConnectedServiceOperationsApiClient(http));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(new ResourceAccessPolicyApiClient(http));
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ResourceScopeInventoryApiClient(http));
        return context;
    }

    private sealed class DatasetHandler : HttpMessageHandler
    {
        public DatasetHandler(bool withParameters = false)
        {
            if (withParameters) Operation = Operation with
            {
                Definition = Operation.Definition with
                {
                    Definition = JsonSerializer.SerializeToElement(new
                    {
                        readOnly = true,
                        inputSchema = new
                        { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" }, additionalProperties = false }
                    })
                }
            };
        }
        public CreateDataSourceRequest? Created { get; private set; }
        public int Activations { get; private set; }
        public int Discoveries { get; private set; }
        public bool NewerOperation { get; set; }
        public bool Existing { get; set; }
        public ConnectedServiceResource Service => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "ConnectedService",
            Uid = Operation.Definition.Service.ServiceUid,
            ScopeRef = ResourceScopeRef.Instance,
            Generation = 1,
            Metadata = new() { Name = "fixture" },
            Definition = new() { DisplayName = "Fixture", Type = null!, Options = JsonSerializer.SerializeToElement(new { }) }
        };
        public ConnectedServiceResource OtherService { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "ConnectedService",
            Uid = Guid.NewGuid(),
            ScopeRef = ResourceScopeRef.Instance,
            Generation = 1,
            Metadata = new() { Name = "other" },
            Definition = new() { DisplayName = "Fixture", Type = null!, Options = JsonSerializer.SerializeToElement(new { }) }
        };
        public ConnectedServiceOperationResource OtherOperation => Operation with
        {
            Uid = OtherService.Uid,
            Metadata = new() { Name = "other-operation" },
            Definition = Operation.Definition with
            {
                DisplayName = "Other operation",
                Service = new()
                { ServiceUid = OtherService.Uid, Generation = 1, Service = new(OtherService.Address, OtherService.ScopeRef!.Value) }
            }
        };
        public DataSourceResource Source => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "DataSource",
            Uid = Service.Uid,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "inventory" },
            Generation = 1,
            Definition = new()
            {
                DisplayName = "Inventory",
                Dataset = new(
                new(new(Storage.ScopeRef!.Value, Storage.Namespace, Storage.Kind, Storage.Name), Storage.Uid, Storage.Generation, Storage.ObservedState!.BindingDigest!),
                new(Write: true), new(new(new(Operation.ScopeRef!.Value, Operation.Namespace, Operation.Kind, Operation.Name), Operation.Uid, Operation.Generation, Operation.Definition.DefinitionDigest), JsonSerializer.SerializeToElement(new { })))
            }
        };
        public StorageResource Storage { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "Storage",
            Uid = Guid.NewGuid(),
            Generation = 3,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "local" },
            Definition = new() { DisplayName = "Local", Type = null!, Configuration = JsonSerializer.SerializeToElement(new { }), Envelope = JsonSerializer.SerializeToElement(new { }) },
            ObservedState = new(StorageAvailability.Ready, DateTimeOffset.UtcNow, "binding", [])
        };
        public ConnectedServiceOperationResource Operation { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "ConnectedServiceOperation",
            Uid = Guid.NewGuid(),
            Generation = 7,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "inventory" },
            Enabled = true,
            Definition = new()
            {
                DisplayName = "Inventory",
                ExternalId = "inventory",
                Type = null!,
                DefinitionDigest = "operation-digest",
                Service = new() { Service = new(new(ResourceNamespace.Default, "ConnectedService", "fixture"), ResourceScopeRef.Instance), ServiceUid = Guid.NewGuid(), Generation = 1 },
                Definition = JsonSerializer.SerializeToElement(new { inputSchema = new { type = "object", additionalProperties = false }, readOnly = true }),
                Provenance = new() { Discovery = new(ResourceScopeRef.Instance, ResourceNamespace.Default, "ConnectedServiceOperationDiscovery", "discovery"), DiscoveryUid = Guid.NewGuid(), ManifestDigest = "manifest", DiscoveredAt = DateTimeOffset.UtcNow }
            },
            ObservedState = new() { Availability = ConnectedServiceOperationAvailability.Available, ObservedAt = DateTimeOffset.UtcNow }
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (request.Method == HttpMethod.Post && path == "/api/datasources")
            {
                Created = await request.Content!.ReadFromJsonAsync<CreateDataSourceRequest>(cancellationToken);
                body = new DataSourceResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = "DataSource",
                    Uid = Guid.NewGuid(),
                    Metadata = new() { Name = Created!.Name },
                    ScopeRef = ResourceScopeRef.Instance,
                    Generation = 1,
                    Definition = Created.Properties
                };
            }
            else if (path == "/api/connected-service-operations") body = new[] { NewerOperation ? Operation with { Generation = Operation.Generation + 1 } : Operation, OtherOperation };
            else if (path == "/api/connected-services/fixture/operations") body = new[] { NewerOperation ? Operation with { Generation = Operation.Generation + 1 } : Operation, OtherOperation };
            else if (path == "/api/connected-services/other/operations") body = new[] { OtherOperation, Operation };
            else if (path == "/api/connected-services") body = new[] { Service, OtherService };
            else if (path == "/api/datasources/inventory" && Existing) body = Source;
            else if (path.EndsWith("/operation-discoveries", StringComparison.Ordinal)) { Discoveries++; body = Array.Empty<ConnectedServiceOperationDiscoveryResource>(); }
            else if (path == "/api/storages") body = new { value = new[] { Storage } };
            else if (path.EndsWith("/readiness", StringComparison.Ordinal)) body = new DataSourceReadiness(true, true, null, []) { CanAcquire = true };
            else if (path.EndsWith("/acquisitions", StringComparison.Ordinal)) body = Array.Empty<DataSourceAcquisitionResource>();
            else if (path.EndsWith("/enabled", StringComparison.Ordinal)) { Activations++; body = Operation; }
            else return new(HttpStatusCode.NotFound);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
            response.Headers.ETag = new("\"fixture\"");
            return response;
        }
    }

    private sealed class ContextProvider : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Administrator", "/instance", ConsoleContextKind.Instance, Guid.NewGuid(), "instance", "Instance",
            Guid.NewGuid(), "operations", "Operations", new HashSet<string> { AuthorizationPermissions.ResourcesWrite }, [], []));
    }
}
