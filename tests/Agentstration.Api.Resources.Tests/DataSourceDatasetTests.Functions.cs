using System.Text;
using System.Text.Json;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Functions;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeExplosionComposesWithFlowsOnTheBindingAndPostAggregate(bool onBinding)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var inputs = await PrepareAsync(factory.Services);
        using var caller = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(inputs.Context);
        var functions = factory.Services.GetRequiredService<TransformationFunctionService>();
        await functions.PutKindAsync(new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.FunctionKind,
            ScopeRef = Owner, Metadata = new() { Name = "native" }, Definition = new("Native", [new("1.0.0", "native", NativeFunctionKindContract.DefinitionSchema)]) }, null, true, default);
        await functions.PutAsync(new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.TransformationFunction,
            ScopeRef = Owner, Metadata = new() { Name = "explode" }, Definition = new("Explode", [new("1.0.0", new("native", "1.0.0", Owner),
                JsonSerializer.SerializeToElement(new { implementation = new { id = "artifact.explode", version = "1.0.0" } }))]) }, null, true, default);
        await functions.PutAsync(new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = FunctionResourceKinds.TransformationFunction,
            ScopeRef = Owner, Metadata = new() { Name = "select" }, Definition = new("Select", [new("1.0.0", new("native", "1.0.0", Owner),
                JsonSerializer.SerializeToElement(new { implementation = new { id = "json.select", version = "1.0.0" } }))]) }, null, true, default);
        var input = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(Source("function-input", inputs, JsonSerializer.SerializeToElement(new { dataset = "functions" })), default);
        var untouched = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(Source("untouched-input", inputs, JsonSerializer.SerializeToElement(new { dataset = "untouched" })), default);
        var batch = JsonSerializer.SerializeToElement(new { artifacts = new[] { new { artifactId = "batch", kind = "inline", disposition = "publishable", mediaType = "application/json",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"results\":[{\"id\":\"a\"},{\"id\":\"b\"}]}")) } } });
        var manifestSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"artifacts":{"type":"array"}},"required":["artifacts"],"additionalProperties":false}""");
        async Task<FlowCallReference> ValueFlow(string name, JsonElement output, bool useFunction = false)
        {
            var flows = factory.Services.GetRequiredService<FlowService>();
            var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = manifestSchema, parameters = new { type = "object" } }, required = new[] { "value", "parameters" } });
            await flows.CreateExactAsync(Owner, new(name, null, "1.0.0", true, new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []), Graph: new()
            {
                EntryStep = "input", InputSchema = schema, OutputSchema = manifestSchema,
                Steps = useFunction
                    ? [new InputFlowStepDefinition { Name = "input", Schema = schema },
                        new TransformFlowStepDefinition { Name = "batch", Mapping = output },
                        new TransformFlowStepDefinition { Name = "select", Function = new("select", "1.0.0", Owner), Schema = manifestSchema,
                            Mapping = JsonSerializer.SerializeToElement(new { value = "${transition.output}", path = "" }) },
                        new OutputFlowStepDefinition { Name = "output", Schema = manifestSchema, OutputMapping = JsonSerializer.SerializeToElement("${transition.output}") }]
                    : [new InputFlowStepDefinition { Name = "input", Schema = schema }, new OutputFlowStepDefinition { Name = "output", Schema = manifestSchema, OutputMapping = output }],
                Transitions = useFunction ? [new("start", "input", "completed", "batch"), new("select", "batch", "completed", "select"), new("done", "select", "completed", "output")]
                    : [new("done", "input", "completed", "output")]
            }), ResourceNamespace.Default, default);
            await flows.PublishVersionExactAsync(Owner, new(name), "1.0.0", true, default);
            return new(name, FlowCallVersionStrategy.Exact, "1.0.0", ScopeRef: Owner);
        }
        var normalize = await ValueFlow("normalize-value", batch, useFunction: true);
        var identity = await ValueFlow("identity-value", JsonSerializer.SerializeToElement("${input.value}"));
        var explode = new FlowCompositionCall { Function = new("explode", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new
            { path = "/results", itemSchema = new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } } }) };
        var sources = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var bindings = new List<KnowledgeDataSourceBinding> { new() { Name = "selected", DataSource = new(input.Value.Name, Owner), RefreshIfNecessary = true,
            Transformations = onBinding ? [new() { Flow = normalize }, explode, new() { Flow = identity }] : [new() { Flow = normalize }] } };
        if (onBinding) bindings.Add(new() { Name = "untouched", DataSource = new(untouched.Value.Name, Owner), RefreshIfNecessary = true });
        var source = await sources.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = KnowledgeResourceKinds.KnowledgeSource, ScopeRef = Owner, Metadata = new() { Name = "function-knowledge" },
            Definition = new()
            {
                DisplayName = "Function knowledge", Output = new(inputs.Storage, new(Write: true)),
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                DataSources = bindings,
                Transformations = onBinding ? [] : [explode, new() { Flow = identity }]
            }
        }, default);
        var id = new KnowledgeSourceId(source.Value.Name, ScopeRef: Owner);
        var projected = await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(id, new(), default);
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, projected.Value.State);
        var snapshot = (await factory.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(id, default))!.Snapshot;
        Assert.HasCount(onBinding ? 3 : 2, snapshot.Artifacts);
        var read = await factory.Services.GetRequiredService<KnowledgeAccessService>().ReadAsync(id, new() { SnapshotId = snapshot.Name }, default);
        Assert.IsTrue(read.Chunks.Any(chunk => chunk.Content.Contains("\"a\"", StringComparison.Ordinal)));
        Assert.IsTrue(read.Chunks.Any(chunk => chunk.Content.Contains("\"b\"", StringComparison.Ordinal)));
        if (onBinding) Assert.IsTrue(read.Chunks.Any(chunk => chunk.Content.Contains("\"untouched\"", StringComparison.Ordinal)));
        var failed = explode with { Parameters = JsonSerializer.SerializeToElement(new { path = "/missing" }) };
        var definition = source.Value.Definition;
        await sources.PutAsync(id, onBinding
            ? definition with { DataSources = definition.DataSources.Select((binding, index) => index == 0
                ? binding with { Transformations = [new() { Flow = normalize }, failed] } : binding).ToArray() }
            : definition with { Transformations = [failed] }, source.ETag, default);
        var failure = await Assert.ThrowsAsync<KnowledgeProjectionException>(() => factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(id, new(), default));
        Assert.AreEqual("function_path_not_found", failure.Code);
        Assert.AreEqual(snapshot.Name, (await factory.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(id, default))!.Snapshot.Name);
    }
}
