using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow("backend_connection_refused", 503)]
    [DataRow("backend_unavailable", 503)]
    [DataRow("backend_timeout", 504)]
    public async Task StructuredAcquisitionRetainsBackendFailureAcrossTheProtectedBinding(string reason, int status)
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agentstration:Aep:SecretAccess:PublicBaseUrl"] = "http://localhost" })));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var inputs = await PrepareAsync(factory.Services);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(inputs.Context);
        using var client = factory.CreateClient(); SelectConsoleContext(client, Owner);
        await using var storage = await StructuredStorageFixture.StartAsync(factory.Services, client, Owner, false);
        storage.Bindings.FailureCode = reason;
        var target = await factory.Services.GetRequiredService<StorageDocumentService>().ResolveTargetAsync(Owner,
            storage.Storage.Namespace, storage.Storage.Name, storage.Collection, default);
        var failure = await Assert.ThrowsAsync<StorageException>(() => factory.Services.GetRequiredService<IStorageDocumentGateway>()
            .PutAsync(target, new("unavailable", DateTimeOffset.UtcNow.AddMinutes(1), "document", JsonSerializer.SerializeToElement(new { })), default));
        Assert.AreEqual("storage_documents_" + reason, failure.Code);
        Assert.AreEqual(status, failure.StatusCode);
        Assert.DoesNotContain("PRIVATE", failure.ToString());
        var source = Source("unavailable-documents", inputs, JsonSerializer.SerializeToElement(new { dataset = "alerts" }));
        source = source with { Definition = source.Definition with { Dataset = source.Definition.Dataset! with
        { Storage = storage.Reference, DocumentCollectionUid = storage.Collection } } };
        var saved = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(source, default);
        using var started = await client.PostAsJsonAsync($"/api/datasources/{saved.Value.Name}/acquisitions?scopeRef=/instance", new StartDataSourceAcquisitionRequest());
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
        var acquisition = (await started.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>())!;
        for (var attempt = 0; attempt < 300 && acquisition.State != DataSourceAcquisitionState.Failed; attempt++)
        {
            await Task.Delay(50);
            acquisition = (await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{acquisition.Name}"))!;
        }
        Assert.AreEqual(DataSourceAcquisitionState.Failed, acquisition.State);
        Assert.AreEqual("storage_documents_" + reason, acquisition.ErrorCode);
        Assert.IsNull(acquisition.Manifest);
    }
}
