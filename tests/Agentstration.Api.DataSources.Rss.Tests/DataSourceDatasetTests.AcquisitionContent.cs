using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    private static async Task VerifyAcquisitionContentAdmissionAsync(IServiceProvider services)
    {
        var flows = services.GetRequiredService<FlowService>();
        var compiler = new FlowCompositionService(flows);
        var template = (await flows.GetVersionExactAsync(ResourceScopeRef.Instance,
            new("data-source-orchestration-builtin"), "1.0.0", default))!.Value;
        var response = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"mediaType":{"type":"string"},"content":{"type":"string"}},"required":["mediaType","content"],"additionalProperties":false}
            """);
        var convert = new FlowCompositionCall { Function = new("rss-2-0-xml-to-json", "1.0.0", Owner) };
        Dictionary<string, IReadOnlyList<FlowCompositionCall>> Bindings(JsonElement schema, params FlowCompositionCall[] calls) => new()
        {
            ["acquisition"] = [new() { Flow = new("fixture-acquire", ScopeRef: Owner) { OutputSchema = schema }, PassInputDirectly = true }],
            ["transformations"] = calls
        };
        var accepted = await compiler.ResolveAsync(template, Bindings(response, convert), Owner, default);
        var selected = accepted.Receipt!.Bindings["transformations"].Single();
        Assert.AreEqual(FlowCompositionFunctionInputSource.AcquisitionContent, selected.FunctionInputSource);
        var function = accepted.Definition.Graph!.Steps.OfType<ToolFlowStepDefinition>().Single(step => step.FunctionBinding is not null);
        Assert.AreEqual("${steps.acquisition.output.content}", function.ArgumentsMapping!.Value.GetProperty("input").GetProperty("value").GetString());
        var replay = await compiler.ResolveAsync(template, accepted.Receipt.Bindings, Owner, default, acceptedSchemaBindings: true);
        Assert.AreEqual(accepted.Receipt.ExecutableHash, replay.Receipt!.ExecutableHash);

        // Missing fields, optional content, nullable content, extra business fields and non-text
        // content never qualify merely because an object happens to contain a content property.
        JsonElement Changed(Action<JsonObject> change)
        {
            var schema = JsonNode.Parse(response.GetRawText())!.AsObject();
            change(schema);
            return JsonSerializer.SerializeToElement(schema);
        }
        JsonElement[] incompatible =
        [
            JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"content":{"type":"string"}},"required":["content"],"additionalProperties":false}"""),
            Changed(schema => schema["required"] = new JsonArray("mediaType")),
            Changed(schema => schema["properties"]!["content"]!["type"] = new JsonArray("string", "null")),
            Changed(schema => schema["properties"]!["content"]!["type"] = "object"),
            Changed(schema => schema["properties"]!["content"]!["type"] = "array"),
            Changed(schema => schema["properties"]!["business"] = new JsonObject { ["type"] = "string" }),
            Changed(schema => schema["additionalProperties"] = true)
        ];
        foreach (var schema in incompatible)
        {
            var failure = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(template, Bindings(schema, convert), Owner, default));
            Assert.AreEqual("flow_composition_function_input_incompatible", failure.Code);
        }
        // An explicit or previously accepted whole-response selection cannot be silently changed.
        foreach (var oldReceipt in new[] { false, true })
        {
            var call = oldReceipt ? convert : convert with { FunctionInputSource = FlowCompositionFunctionInputSource.PreviousResult };
            var failure = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(template,
                Bindings(response, call), Owner, default, acceptedSchemaBindings: oldReceipt));
            Assert.AreEqual("flow_composition_function_input_incompatible", failure.Code);
        }
        var ordinary = template with
        {
            Graph = template.Graph! with
            {
                Steps = template.Graph.Steps.Select(step => step is FlowCompositionPointStepDefinition point && point.Point == "acquisition"
                ? point with { Point = "prepare" } : step).ToArray()
            }
        };
        var ordinaryBindings = Bindings(response, convert);
        ordinaryBindings["prepare"] = ordinaryBindings["acquisition"];
        ordinaryBindings.Remove("acquisition");
        var ordinaryFailure = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(ordinary, ordinaryBindings, Owner, default));
        Assert.AreEqual("flow_composition_function_input_incompatible", ordinaryFailure.Code);

        var select = new FlowCompositionCall
        {
            Function = new("json-select-builtin", "1.0.0", Owner),
            Parameters = JsonSerializer.SerializeToElement(new { path = "/content" })
        };
        var explicitSelection = await compiler.ResolveAsync(template, Bindings(response, select, convert), Owner, default);
        Assert.IsTrue(explicitSelection.Receipt!.Bindings["transformations"].All(call => call.FunctionInputSource == FlowCompositionFunctionInputSource.PreviousResult));
        var selectStep = explicitSelection.Definition.Graph!.Steps.OfType<ToolFlowStepDefinition>().First(step => step.FunctionBinding is not null);
        Assert.AreEqual("${steps.acquisition.output}", selectStep.ArgumentsMapping!.Value.GetProperty("input").GetProperty("value").GetString());
        var forbiddenLater = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => compiler.ResolveAsync(template,
            Bindings(response, select, convert with { FunctionInputSource = FlowCompositionFunctionInputSource.AcquisitionContent }), Owner, default));
        Assert.AreEqual("flow_composition_function_input_incompatible", forbiddenLater.Code);
        var text = await compiler.ResolveAsync(template, Bindings(JsonSerializer.SerializeToElement(new { type = "string" }), convert), Owner, default);
        Assert.AreEqual(FlowCompositionFunctionInputSource.PreviousResult, text.Receipt!.Bindings["transformations"].Single().FunctionInputSource);

        // A builtin acquisition Flow has a generic object output; its compatible exact Operation
        // contract supplies the guaranteed fields without replacing incompatible explicit contracts.
        var generic = await PublishAsync(services, "generic-text-acquisition", DataSourceDatasetCompositionService.FlowContract,
            JsonSerializer.SerializeToElement(new { mediaType = "text/plain", content = "example" }));
        var genericBindings = Bindings(response, convert);
        genericBindings["acquisition"] = [genericBindings["acquisition"][0] with
        { Flow = new(generic.Flow.Name, ScopeRef: Owner) { OutputSchema = response } }];
        var refined = await compiler.ResolveAsync(template, genericBindings, Owner, default);
        Assert.AreEqual(FlowCompositionFunctionInputSource.AcquisitionContent, refined.Receipt!.Bindings["transformations"].Single().FunctionInputSource);
    }
}
