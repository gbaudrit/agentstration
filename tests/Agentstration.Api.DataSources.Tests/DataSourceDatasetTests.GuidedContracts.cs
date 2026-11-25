using System.Text.Json;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Functions;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    [DataRow("application/xml")]
    [DataRow("application/rss+xml")]
    public async Task GuidedXmlSequenceDerivesExactItemEvidenceAndUsesTheExistingEngines(string mediaType)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var transport = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"mediaType":{"type":"string"},"content":{"type":"string"}},"required":["mediaType","content"],"additionalProperties":false}""");
        var raw = JsonSerializer.SerializeToElement(new { mediaType, content = "<root><item id=\"one\"/><item id=\"two\"/></root>" });
        var fixture = await PrepareAsync(factory.Services, emptyInput: true, operationResult: raw, declareAcquisitionOutput: false, operationOutputSchema: transport);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
        async Task<SchemaVersionReference> PublishSchema(string name, string format, JsonElement content)
        {
            _ = await schemas.CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SchemaResourceKinds.Schema,
                ScopeRef = Owner,
                Metadata = new() { Name = name },
                Definition = new() { DisplayName = name, Format = format, Content = content }
            }, default);
            _ = await schemas.PublishAsync(Owner, ResourceNamespace.Default, name, "1.0.0", default);
            return new() { Name = name, Version = "1.0.0", ScopeRef = Owner, Namespace = ResourceNamespace.Default };
        }
        var input = await PublishSchema("guided-xml", BuiltinXmlSchemaFormatHandler.Format, JsonSerializer.SerializeToElement("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="root"><xs:complexType><xs:sequence><xs:element name="item" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:attribute name="id" type="xs:string" use="required"/></xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element></xs:schema>"""));
        var output = await PublishSchema("guided-json", SchemaFormatIds.JsonSchema, JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"rows":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}}},"required":["rows"],"additionalProperties":false}"""));
        var declaration = await PublishSchema("guided-mapping", TransformationSchemaFormatHandler.Format,
            JsonSerializer.SerializeToElement(new TransformationSchemaDeclaration(new("xml-to-json-builtin", "1.0.0", Owner), input, output,
                JsonSerializer.Deserialize<JsonElement>("""{"root":{"path":"/root","type":"object","fields":{"rows":{"path":"item","type":"array","fields":{"id":{"path":"@id","type":"string"}}}}}}""")), JsonSerializerOptions.Web));
        var function = await factory.Services.GetRequiredService<TransformationFunctionService>().ExposeAsync(new("guided-converter", "Guided converter", "1.0.0", declaration, Owner, ResourceNamespace.Default), default);
        var operations = factory.Services.GetRequiredService<ConnectedServiceOperationService>();
        var discoveries = factory.Services.GetRequiredService<ConnectedServiceOperationDiscoveryService>();
        var stored = (await discoveries.GetOperationAsync(Owner, ResourceNamespace.Default, fixture.Operation.Name, default))!;
        var adopted = await operations.AdoptContentAsync(fixture.OperationReference.Operation, new(mediaType, "/content", input), stored.ETag, default);
        Assert.IsNull((await operations.ResolveAsync(fixture.OperationReference, Owner, true, default)).Operation.Definition.AdoptedContentContract);
        var acceptedOperation = new ConnectedServiceOperationRevisionReference(fixture.OperationReference.Operation, adopted.Value.Uid, adopted.Value.Generation, adopted.Value.Definition.DefinitionDigest);
        var currentFixture = fixture with { Operation = adopted.Value, OperationReference = acceptedOperation };
        var calls = new FlowCompositionCall[]
        {
            new() { Function = new(function.Value.Name, "1.0.0", Owner) },
            new() { Function = new("json-explode-builtin", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new { path = "/rows" }) }
        };
        var sequences = factory.Services.GetRequiredService<ISourceSequenceService>();
        var snapshot = new SchemaBindingSnapshot("operation.transport", SchemaFormatIds.JsonSchema, BuiltinJsonSchemaFormatHandler.Draft202012, transport,
            JsonSchemaSelection.DefinitionHash(SchemaFormatIds.JsonSchema, BuiltinJsonSchemaFormatHandler.Draft202012, transport));
        var request = new SourceSequenceRequest(Owner, ResourceNamespace.Default, calls, snapshot, true, adopted.Value.Definition.AdoptedContentContract!.Schema);
        var analysis = await sequences.AnalyzeAsync(request, default);
        Assert.AreEqual(SequenceCompatibility.Verified, analysis.Steps[0].Compatibility);
        var candidate = analysis.Steps[1].Collections.Single();
        Assert.AreEqual("/rows", candidate.DataPath);
        Assert.AreEqual("/properties/rows/items", candidate.Item.Source!.SchemaPath);
        Assert.AreEqual(output.Name, candidate.Item.Source.Name);
        var replay = await sequences.ReplayAsync(request, raw, "same-raw-sample", default);
        Assert.IsNull(replay.ErrorCode);
        Assert.HasCount(3, replay.Values);
        var records = JsonRecordProjection.Parse(replay.Values[2])!;
        Assert.AreEqual("one", records.Value[0].GetProperty("id").GetString());
        Assert.AreEqual("two", records.Value[1].GetProperty("id").GetString());
        Assert.AreEqual("/properties/rows/items", records.RecordContract.SchemaEvidence!.SchemaPath);
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var desired = Source("guided-production", currentFixture, Empty);
        desired = desired with { Definition = desired.Definition with { Dataset = desired.Definition.Dataset! with { Transformations = calls } } };
        var saved = await sources.CreateAsync(desired, default);
        var resolved = await factory.Services.GetRequiredService<DataSourceDatasetCompositionService>().ResolveAsync(saved.Value, Owner, default);
        var call = resolved.Composition!.Receipt!.Bindings["transformations"][1];
        Assert.AreEqual(output.Name, call.FunctionSchemaSnapshots!["itemSchema"].Source!.Name);
        Assert.AreEqual(candidate.Item.ParentDefinitionHash, call.FunctionSchemaSnapshots["itemSchema"].ParentDefinitionHash);
        _ = await schemas.PublishAsync(Owner, ResourceNamespace.Default, input.Name, "2.0.0", default);
        Assert.AreEqual("1.0.0", (await operations.ResolveAsync(acceptedOperation, Owner, true, default)).Operation.Definition.AdoptedContentContract!.Schema.Source!.Version);
        var next = await operations.AdoptContentAsync(acceptedOperation.Operation, new(mediaType, "/content", input with { Version = "2.0.0" }), adopted.ETag, default);
        Assert.AreEqual("2.0.0", next.Value.Definition.AdoptedContentContract!.Schema.Source!.Version);
        var discovery = await discoveries.StartAsync(Owner, fixture.Operation.Definition.Service.Service.Address.Name, null, null, default);
        for (var attempt = 0; attempt < 1000 && discovery.Value.CompletedAt is null; attempt++)
        {
            await Task.Delay(20);
            discovery = await discoveries.GetAsync(ResourceNamespace.Default, discovery.Value.Name, default);
        }
        Assert.AreEqual(ConnectedServiceOperationDiscoveryState.Succeeded, discovery.Value.State, discovery.Value.ErrorMessage);
        var rediscovered = (await discoveries.GetOperationAsync(Owner, ResourceNamespace.Default, fixture.Operation.Name, default))!;
        Assert.AreEqual(next.Value.Generation, rediscovered.Value.Generation);
        Assert.AreEqual(next.Value.Definition.AdoptedContentContract!.Schema.DefinitionHash, rediscovered.Value.Definition.AdoptedContentContract!.Schema.DefinitionHash);
        Assert.AreEqual("2.0.0", rediscovered.Value.Definition.AdoptedContentContract!.Schema.Source!.Version);
        Assert.AreEqual("1.0.0", (await operations.ResolveAsync(acceptedOperation, Owner, true, default)).Operation.Definition.AdoptedContentContract!.Schema.Source!.Version);
    }
}
