using System.Text;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task KnowledgePreviewConsumesTransformedResultsOnceAndPreservesNamedRequiredPolicies()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var owner = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var management = factory.Services.GetRequiredService<DataSourceManagementService>();
        async Task<DataSourceResource> Create(string name, string outputName)
        {
            var desired = Source(name, fixture, JsonSerializer.SerializeToElement(new { dataset = name }));
            desired = desired with
            {
                Definition = desired.Definition with
                {
                    Dataset = desired.Definition.Dataset! with
                    {
                        Transformations = [new() { Function = new("json-project-builtin", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new { properties = new[] { new { name = outputName, path = "/dataset" } } }) }]
                    }
                }
            };
            return (await management.CreateAsync(desired, default)).Value;
        }
        var left = await Create("preview-left", "leftValue");
        var right = await Create("preview-right", "rightValue");
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var leftAcquisition = await AcquireAsync(client, left.Name);
        var rightAcquisition = await AcquireAsync(client, right.Name);
        var samples = factory.Services.GetRequiredService<SourceSampleService>();
        var leftSample = await samples.PrepareConsumedAsync(new(new(left.Name, Owner), leftAcquisition.Name, leftAcquisition.Namespace), default);
        var rightSample = await samples.PrepareConsumedAsync(new(new(right.Name, Owner), rightAcquisition.Name, rightAcquisition.Namespace), default);
        Assert.IsFalse(leftSample.IsRawOperationResponse);
        var bytes = Convert.FromBase64String(leftSample.Value!.Value.GetProperty("artifacts")[0].GetProperty("contentBase64").GetString()!);
        var consumed = JsonSerializer.Deserialize<JsonElement>(bytes);
        Assert.AreEqual("preview-left", consumed[0].GetProperty("leftValue").GetString());
        Assert.IsFalse(consumed[0].TryGetProperty("dataset", out _));
        var properties = new KnowledgeSourceProperties
        {
            DisplayName = "Preview only",
            ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
            DataSources =
            [
                new() { Name = "namedLeft", DataSource = new(left.Name, Owner), Transformations = [new() { Function = new("json-select-builtin", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new { path = "" }) }] },
                new() { Name = "namedRight", DataSource = new(right.Name, Owner) },
                new() { Name = "optional", DataSource = new(right.Name, Owner), Required = false }
            ]
        };
        var preview = factory.Services.GetRequiredService<KnowledgePreviewService>();
        var request = new PrepareKnowledgePreviewRequest(ResourceNamespace.Default, properties,
            new Dictionary<string, string> { ["namedLeft"] = leftSample.Id, ["namedRight"] = rightSample.Id });
        var aggregate = await preview.PrepareAsync(request, default);
        Assert.AreEqual(2, aggregate.Sample.Value!.Value.GetProperty("artifacts").GetArrayLength());
        CollectionAssert.AreEquivalent(new[] { "namedLeft", "namedRight" }, aggregate.Sample.InputSamples!.Keys.ToArray());
        Assert.AreEqual("optional", aggregate.Issues.Single().BindingName);
        Assert.IsFalse(aggregate.Issues.Single().Required);
        var missing = await Assert.ThrowsExactlyAsync<KnowledgeSourceValidationException>(() => preview.PrepareAsync(request with { BindingSamples = new Dictionary<string, string> { ["namedLeft"] = leftSample.Id } }, default));
        Assert.AreEqual("knowledge_preview_required_input_unavailable", missing.Code);
        var global = await samples.ReplayAsync(aggregate.Sample.Id, new(ResourceNamespace.Default, []), default);
        Assert.IsNull(global.ErrorCode);
        using var workspace = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context);
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        Assert.IsEmpty(await resources.ListExactAsync<KnowledgeSnapshotResource>(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), KnowledgeResourceKinds.KnowledgeSnapshot, 0, 100, default));
        var acquisitions = await resources.ListExactAsync<DataSourceAcquisitionResource>(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), DataSourceResourceKinds.DataSourceAcquisition, 0, 100, default);
        Assert.HasCount(2, acquisitions);
        Assert.AreEqual(leftAcquisition.CompletedAt, acquisitions.Single(a => a.Value.Uid == leftAcquisition.Uid).Value.CompletedAt);
    }
}
