using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Functions;
using Agentstration.Infrastructure;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task StartupReactivatesCurrentDataSourceOrchestrationAndPreservesPublishedLegacyVersion()
    {
        await using var factory = Factory();
        _ = await GetBootstrapContextAsync(factory);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        using var authority = factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("knowledge.platform"));
        var flows = factory.Services.GetRequiredService<FlowService>();
        FlowId id = new("data-source-orchestration-builtin");
        var current = (await flows.GetExactAsync(Owner, id, default))!;
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var legacy = current.Value.Graph! with { OutputSchema = schema, Steps = current.Value.Graph.Steps.Select(step => step switch
        {
            FlowCompositionPointStepDefinition point when point.Point == "transformations" => point with { OutputSchema = schema },
            OutputFlowStepDefinition output => output with { Schema = schema },
            _ => step
        }).ToArray() };
        _ = await flows.UpdateExactAsync(Owner, id, new(current.Value.Description, "1.0.0", true, current.Value.Definition, current.Value.Metadata, legacy), current.ETag, default);
        var original = await flows.PublishVersionExactAsync(Owner, id, "1.0.0", true, default);
        await factory.Services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        var restored = (await flows.GetExactAsync(Owner, id, default))!;
        Assert.AreEqual(current.Value.Uid, restored.Value.Uid);
        Assert.AreEqual("1.1.0", restored.Value.ActiveVersion);
        Assert.AreEqual(original.Value.DefinitionHash, (await flows.GetVersionExactAsync(Owner, id, "1.0.0", default))!.Value.DefinitionHash);
    }

    [TestMethod]
    [DataRow("/results/items", JsonValueKind.Array, false)]
    [DataRow("/name", JsonValueKind.String, false)]
    [DataRow("/results", JsonValueKind.Object, false)]
    [DataRow("/results/total", JsonValueKind.Number, false)]
    [DataRow("/results/items", JsonValueKind.Array, true)]
    public async Task DatasetCreatesAndStoresSelectedJsonValuesWithPinnedNativeFunctions(string path, JsonValueKind kind, bool filter)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services, operationResult: JsonSerializer.SerializeToElement(new
        {
            name = "alerts",
            results = new { total = 2, items = new[] { new { id = "a", active = true }, new { id = "b", active = false } } }
        }), declareAcquisitionOutput: false);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var functions = factory.Services.GetRequiredService<TransformationFunctionService>();
        var selected = await functions.ResolveAsync(new("json-select-builtin", "1.0.0", Owner), Owner, default);
        var calls = new List<FlowCompositionCall> { new() { Function = selected.Reference, Parameters = JsonSerializer.SerializeToElement(new { path }) } };
        if (filter) calls.Add(new() { Function = new("collection-filter-builtin", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new { path = "/active", equals = true }) });
        var source = Source("select-alerts", fixture, JsonSerializer.SerializeToElement(new { dataset = "alerts" }));
        var definition = source.Definition with { Dataset = source.Definition.Dataset! with
        { Compositions = new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["transformations"] = calls } } };
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        using var response = await client.PostAsJsonAsync("/api/datasources", new CreateDataSourceRequest(source.Name, definition, ScopeRef: Owner));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var acquisition = await AcquireAsync(client, source.Name);
        Assert.AreEqual("object", acquisition.Dataset!.OperationOutputSchema!.Value.GetProperty("type").GetString());
        Assert.AreEqual("1.1.0", acquisition.Dataset.Composition!.Orchestration.Version);
        var content = await factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(acquisition, 0, 65536, default);
        using var json = JsonDocument.Parse(Convert.FromBase64String(content.ContentBase64));
        Assert.AreEqual(kind, json.RootElement.ValueKind);
        if (kind == JsonValueKind.Array) Assert.AreEqual(filter ? 1 : 2, json.RootElement.GetArrayLength());
        if (kind == JsonValueKind.String) Assert.AreEqual("alerts", json.RootElement.GetString());
        if (kind == JsonValueKind.Number) Assert.AreEqual(2, json.RootElement.GetInt32());
    }

    [TestMethod]
    public async Task DatasetCompositionFailureExposesItsSafeUnderlyingReason()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        var source = Source("invalid-expansion", fixture, JsonSerializer.SerializeToElement(new { dataset = "alerts" }));
        var definition = source.Definition with { Dataset = source.Definition.Dataset! with
        { Compositions = new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["transformations"] = [new()
            { Function = new("artifact-explode-builtin", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new { path = "/results/items" }) }] } } };
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        using var response = await client.PostAsJsonAsync("/api/datasources", new CreateDataSourceRequest(source.Name, definition, ScopeRef: Owner));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("flow_composition_function_input_incompatible", problem.RootElement.GetProperty("compositionErrorCode").GetString());
        Assert.IsTrue(problem.RootElement.GetProperty("type").GetString()!.EndsWith("data_source_composition_invalid", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DatasetSavesAndExecutesNativeProjectionWithNestedPropertyParameters(bool declareAcquisitionOutput)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services, operationResult: JsonSerializer.SerializeToElement(new
        { results = new { items = new[] { new { id = "alert-1" }, new { id = "alert-2" } } }, ignored = "envelope" }), declareAcquisitionOutput: declareAcquisitionOutput);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var management = factory.Services.GetRequiredService<DataSourceManagementService>();
        var source = await management.CreateAsync(Source("project-alerts", fixture, JsonSerializer.SerializeToElement(new { dataset = "alerts" })), default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var definition = source.Value.Definition with { Dataset = source.Value.Definition.Dataset! with
        {
            Compositions = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>
            { ["transformations"] = [new() { Function = new("json-project-builtin", "1.0.0", Owner),
                Parameters = JsonSerializer.SerializeToElement(new { properties = new[] { new { name = "results", path = "/results/items" } } }) }] }
        } };
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/datasources/project-alerts?scopeRef=%2Finstance")
        { Content = JsonContent.Create(new PutDataSourceRequest(definition)) };
        request.Headers.TryAddWithoutValidation("If-Match", source.ETag);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var acquisition = await AcquireAsync(client, source.Value.Name);
        var content = await factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(acquisition, 0, 65536, default);
        using var json = JsonDocument.Parse(Convert.FromBase64String(content.ContentBase64));
        Assert.AreEqual(2, json.RootElement.GetProperty("results").GetArrayLength());
        Assert.AreEqual("alert-1", json.RootElement.GetProperty("results")[0].GetProperty("id").GetString());
        Assert.IsFalse(json.RootElement.TryGetProperty("ignored", out _));
    }
}
