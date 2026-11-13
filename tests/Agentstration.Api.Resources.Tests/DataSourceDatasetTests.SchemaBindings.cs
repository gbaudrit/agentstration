using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task AcquisitionPinsParentVersionAndSubschemaInImmutableArtifactEvidence()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
        var parent = await schemas.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = SchemaResourceKinds.Schema,
            ScopeRef = Owner, Metadata = new() { Name = "dataset-response" },
            Definition = new()
            {
                DisplayName = "Dataset response", Format = SchemaFormatIds.JsonSchema,
                Content = JsonSerializer.Deserialize<JsonElement>("""
                    {"type":"object","properties":{"document":{"type":"object","properties":{"dataset":{"type":"string"}},"required":["dataset"]}}}
                    """)
            }
        }, default);
        var versionA = await schemas.PublishAsync(Owner, ResourceNamespace.Default, parent.Value.Name, "A", default);
        var binding = new SchemaBinding { Reference = new() { Name = parent.Value.Name, Version = "A", ScopeRef = Owner, SchemaPath = "/properties/document" } };
        var desired = Source("bound-input", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" }));
        desired = desired with { Definition = desired.Definition with { Dataset = desired.Definition.Dataset! with
        { Acquisition = desired.Definition.Dataset.Acquisition with { OutputSchemaBinding = binding } } } };
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var source = await sources.CreateAsync(desired, default);
        var pin = source.Value.Definition.Dataset!.Acquisition.OutputSchemaSnapshot!;
        Assert.AreEqual(versionA.Value.DefinitionHash, pin.ParentDefinitionHash);
        Assert.AreEqual(parent.Value.Uid, pin.SchemaUid);

        var changed = await schemas.PutAsync(Owner, parent.Value.Namespace, parent.Value.Name,
            parent.Value.Definition with { Content = JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"document":{"type":"object","properties":{"dataset":{"type":"integer"}},"required":["dataset"]}}}
                """) }, parent.ETag, default);
        _ = await schemas.PublishAsync(Owner, ResourceNamespace.Default, parent.Value.Name, "B", default);

        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var acquired = await AcquireAsync(client, source.Value.Name);
        Assert.AreEqual("A", acquired.Dataset!.OutputSchemaSnapshot!.Source!.Version);
        using var workspace = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context);
        var receipt = (await factory.Services.GetRequiredService<IResourceStore>().GetExactAsync<FlowRunArtifactResource>(
            new(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), ResourceNamespace.Default,
                ArtifactResourceKinds.FlowRunArtifact, acquired.Manifest!.Artifacts.Single().ArtifactId), default))!.Value.Receipt;
        Assert.IsTrue(receipt.DataContract!.Validated);
        Assert.AreEqual("A", receipt.DataContract.SchemaEvidence!.Version);
        Assert.AreEqual("/properties/document", receipt.DataContract.SchemaEvidence.SchemaPath);
        Assert.AreEqual(versionA.Value.DefinitionHash, receipt.DataContract.SchemaEvidence.ParentDefinitionHash);
        Assert.AreEqual(pin.DefinitionHash, receipt.DataContract.SchemaEvidence.DefinitionHash);
        Assert.AreEqual("string", receipt.DataContract.Schema!.Value.GetProperty("properties").GetProperty("dataset").GetProperty("type").GetString());
        Assert.IsNotNull(changed);
        using var instance = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var bindingB = binding with { Reference = binding.Reference! with { Version = "B" } };
        var invalid = desired with { Metadata = new() { Name = "bound-incompatible-output" },
            Definition = desired.Definition with { Dataset = desired.Definition.Dataset! with
            { Acquisition = desired.Definition.Dataset.Acquisition with { OutputSchemaBinding = bindingB } } } };
        var sourceB = await sources.CreateAsync(invalid, default);
        using var started = await client.PostAsJsonAsync($"/api/datasources/{sourceB.Value.Name}/acquisitions?scopeRef=/instance", new StartDataSourceAcquisitionRequest());
        Assert.AreEqual(HttpStatusCode.Accepted, started.StatusCode);
        var pending = (await started.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>())!;
        for (var attempt = 0; attempt < 300 && pending.State != DataSourceAcquisitionState.Failed; attempt++)
        {
            pending = (await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{pending.Name}"))!;
            if (pending.State != DataSourceAcquisitionState.Failed) await Task.Delay(20);
        }
        Assert.AreEqual(DataSourceAcquisitionState.Failed, pending.State);
        Assert.AreEqual("data_contract_violation", pending.ErrorCode);
        Assert.AreEqual("$.dataset", pending.ContractFailure!.Path);
        Assert.IsNull(pending.Manifest);
        Assert.AreEqual("A", receipt.DataContract.SchemaEvidence.Version);
    }
}
