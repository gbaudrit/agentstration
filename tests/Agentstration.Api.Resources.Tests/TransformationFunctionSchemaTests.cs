using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class TransformationFunctionSchemaTests : ResourceManagementApiTestBase
{
    [TestMethod]
    public async Task SourceAcceptanceAndFlowPublicationRejectUnresolvableBindingsAndIgnoreCallerSnapshots()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder.UseSetting(
            $"{Agentstration.Functions.FunctionContentOptions.SectionName}:MaximumValueBytes", "1048576"));
        var context = await GetBootstrapContextAsync(factory);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context);
        var owner = ResourceScopeRef.Instance;
        var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
        var schema = await schemas.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SchemaResourceKinds.Schema,
            ScopeRef = owner,
            Metadata = new() { Name = "function-items" },
            Definition = new() { DisplayName = "Items", Format = SchemaFormatIds.JsonSchema, Content = Json("""{"type":"string"}""") }
        }, default);
        await schemas.PublishAsync(owner, ResourceNamespace.Default, schema.Value.Name, "A", default);
        var valid = new SchemaBinding { Reference = new() { Name = schema.Value.Name, Version = "A", ScopeRef = owner } };
        var snapshot = await factory.Services.GetRequiredService<ISchemaBindingResolver>().ResolveAsync("$.itemSchema", valid,
            new(owner, ResourceNamespace.Default, SchemaFormatIds.JsonSchema), default);
        var invalid = new (SchemaBinding Binding, string Code)[]
        {
            (valid with { Reference = valid.Reference! with { Name = "missing" } }, "schema_reference_not_found"),
            (valid with { Reference = valid.Reference! with { Version = "missing" } }, "schema_version_not_found"),
            (valid with { Reference = valid.Reference! with { SchemaPath = "/properties/missing" } }, "schema_pointer_not_found"),
            (valid with { Reference = valid.Reference! with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } }, "schema_reference_forbidden"),
            (new() { Inline = new() { Format = "xml", Content = Json("{}") } }, "schema_format_incompatible"),
            (new() { Inline = new() { Format = SchemaFormatIds.JsonSchema, Content = Json("""{"type":"unknown"}""") } }, "schema_definition_invalid"),
            (valid with { Inline = new() { Format = SchemaFormatIds.JsonSchema, Content = Json("{}") } }, "schema_binding_ambiguous")
        };
        var flows = factory.Services.GetRequiredService<FlowService>();
        var sequences = factory.Services.GetRequiredService<IFlowCompositionResolver>();
        for (var index = 0; index < invalid.Length; index++)
        {
            var (binding, code) = invalid[index];
            var function = new Agentstration.Functions.Contracts.FunctionReference("artifact-explode-builtin", "1.0.0", owner);
            var call = new FlowCompositionCall
            {
                Function = function,
                Parameters = JsonSerializer.SerializeToElement(new { path = "", itemSchema = binding }, JsonSerializerOptions.Web),
                FunctionSchemaSnapshots = new Dictionary<string, SchemaBindingSnapshot> { ["itemSchema"] = snapshot }
            };
            var sequenceError = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => sequences.ResolveAsync(owner,
                ResourceNamespace.Default, new("content-transformation-sequence-builtin", ScopeRef: owner),
                new Dictionary<string, IReadOnlyList<FlowCompositionCall>> { ["transformations"] = [call] }, default));
            Assert.AreEqual(code, sequenceError.Code, $"Source binding {index}");
            var name = "invalid-function-schema-" + index;
            await flows.CreateExactAsync(owner, new(name, null, "1.0.0", true,
                new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []), Graph: new()
                {
                    EntryStep = "input",
                    Steps = [new InputFlowStepDefinition { Name = "input" },
                        new TransformFlowStepDefinition { Name = "explode", Function = function,
                            Mapping = Json("""{"value":{"content":[]},"path":""}"""),
                            FunctionSchemas = new Dictionary<string, SchemaBinding> { ["itemSchema"] = binding },
                            FunctionSchemaSnapshots = call.FunctionSchemaSnapshots },
                        new OutputFlowStepDefinition { Name = "output" }],
                    Transitions = [new("start", "input", "completed", "explode"), new("end", "explode", "completed", "output")]
                }), ResourceNamespace.Default, default);
            var flowError = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => flows.PublishVersionExactAsync(owner, new(name), "1.0.0", true, default));
            Assert.AreEqual(code, flowError.Code, $"Flow binding {index}");
        }
        var functionContract = await factory.Services.GetRequiredService<Agentstration.Functions.TransformationFunctionService>()
            .ResolveAsync(new("artifact-explode-builtin", "1.0.0", owner), owner, default);
        await flows.CreateExactAsync(owner, new("forged-function-snapshot", null, "1.0.0", true,
            new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []), Graph: new()
            {
                EntryStep = "input",
                Steps = [new InputFlowStepDefinition { Name = "input" },
                    new ToolFlowStepDefinition { Name = "forged", Tool = new("agentstration.transformation.invoke"), FunctionBinding = functionContract,
                        FunctionSchemaSnapshots = new Dictionary<string, SchemaBindingSnapshot> { ["itemSchema"] = snapshot } },
                    new OutputFlowStepDefinition { Name = "output" }],
                Transitions = [new("start", "input", "completed", "forged"), new("end", "forged", "success", "output")]
            }), ResourceNamespace.Default, default);
        var forged = await Assert.ThrowsExactlyAsync<FlowValidationException>(() => flows.PublishVersionExactAsync(owner, new("forged-function-snapshot"), "1.0.0", true, default));
        Assert.AreEqual("function_binding_reserved", forged.Code);

        var largeSchema = await schemas.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SchemaResourceKinds.Schema,
            ScopeRef = owner,
            Metadata = new() { Name = "large-function-schema" },
            Definition = new()
            {
                DisplayName = "Large schema",
                Format = SchemaFormatIds.JsonSchema,
                Content = JsonSerializer.SerializeToElement(new { type = "string", description = new string('x', 64_000) })
            }
        }, default);
        await schemas.PublishAsync(owner, ResourceNamespace.Default, largeSchema.Value.Name, "A", default);
        var largeBinding = new SchemaBinding { Reference = new() { Name = largeSchema.Value.Name, Version = "A", ScopeRef = owner } };
        var largeSnapshot = await factory.Services.GetRequiredService<ISchemaBindingResolver>().ResolveAsync("$.itemSchema", largeBinding,
            new(owner, ResourceNamespace.Default, SchemaFormatIds.JsonSchema), default);
        var oversized = await Assert.ThrowsExactlyAsync<Agentstration.Functions.Abstractions.TransformationFunctionException>(() =>
            factory.Services.GetRequiredService<Agentstration.Functions.TransformationFunctionService>().InvokeAsync(functionContract, owner,
                JsonSerializer.SerializeToElement(new { value = new { content = new string('x', 1_000_000) }, path = "", itemSchema = largeBinding }, JsonSerializerOptions.Web),
                default, schemaBindings: new Dictionary<string, SchemaBindingSnapshot> { ["itemSchema"] = largeSnapshot }));
        Assert.AreEqual("function_value_too_large", oversized.Code, "Resolved inline schema content remains inside the native invocation budget.");
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
