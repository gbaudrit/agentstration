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
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='data-source-start-acquisition']").HasAttribute("disabled")));
    }

    private sealed class DatasetHandler : HttpMessageHandler
    {
        public CreateDataSourceRequest? Created { get; private set; }
        public int Activations { get; private set; }
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
            else if (path == "/api/connected-service-operations") body = new[] { Operation };
            else if (path == "/api/connected-services") body = Array.Empty<ConnectedServiceResource>();
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
