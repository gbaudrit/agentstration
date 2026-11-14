using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
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
