using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Functions;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Builtins;
using Agentstration.Identity.Contracts;
using Agentstration.Schemas;

namespace Agentstration.Infrastructure.Functions;

/// <summary>Changes record granularity without multiplying logical Artifact identities.</summary>
public sealed class JsonExplodeFunction(ArtifactManagementService artifacts, IArtifactDurableStore durable,
    ICurrentRequestContext current, IRequestContextScopeFactory scopes, NativeFunctionExecutionContext execution) : INativeTransformationFunction
{
    private readonly ArtifactFunctionContentReader reader = new(artifacts, durable, current, scopes);
    public NativeFunctionDescriptor Descriptor { get; } = FunctionSchemas.Descriptor("json.explode", """
        {"value":{},"path":{"type":"string","maxLength":1024},"itemSchema":{"type":"object","x-agentstration-schema-binding":true,"properties":{"inline":{"type":["object","null"],"properties":{"format":{"type":"string"},"dialect":{"type":["string","null"]},"content":{}},"required":["format","content"],"additionalProperties":false},"reference":{"type":["object","null"],"properties":{"name":{"type":"string","minLength":1},"version":{"type":"string","minLength":1},"scopeRef":{"type":["string","null"]},"namespace":{"type":["string","null"]},"schemaPath":{"type":["string","null"],"maxLength":1024}},"required":["name","version"],"additionalProperties":false}},"additionalProperties":false}}
        """, ["value", "path", "itemSchema"]);

    public async ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var path = input.GetProperty("path").GetString()!;
        if (execution.InvocationId is null || execution.SchemaBindings?.TryGetValue("itemSchema", out var snapshot) != true)
            throw Error("function_schema_snapshot_missing");
        var contract = DataContracts.Declared(JsonSchemaSelection.Select(snapshot!.Content), "function:json.explode/record", "application/json")!;
        if (snapshot.Source is { ScopeRef: { } scope, Namespace: { } ns } source
            && snapshot.SchemaUid is { } uid && snapshot.SchemaGeneration is { } generation)
            contract = contract with { SchemaEvidence = new(source.Name, source.Version, scope, ns, uid, generation,
                snapshot.ParentDefinitionHash ?? snapshot.DefinitionHash, source.SchemaPath, snapshot.DefinitionHash) };
        contract = contract with { Validated = true };
        var value = input.GetProperty("value");
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("artifacts", out var entries)
            || value.EnumerateObject().Count() != 1)
            return JsonSerializer.SerializeToElement(Project(value, path, contract, cancellationToken), JsonSerializerOptions.Web);
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 100)
            throw Error("function_json_records_invalid");
        var output = new List<object>();
        foreach (var entry in entries.EnumerateArray())
        {
            var parentId = entry.GetProperty("artifactId").GetString()!;
            var bytes = await reader.ReadAsync(entry, parentId, cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            var recordInput = entry.TryGetProperty("recordContract", out var rowContract) && rowContract.ValueKind == JsonValueKind.Object;
            var records = recordInput ? ProjectRecords(document.RootElement, path, contract, cancellationToken)
                : Project(document.RootElement, path, contract, cancellationToken);
            var content = JsonSerializer.SerializeToUtf8Bytes(records.Value);
            var digest = Convert.ToHexStringLower(SHA256.HashData(content));
            var identity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { execution.InvocationId, parentId, inputDigest = Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), path, contract.Digest })));
            output.Add(new
            {
                artifactId = "records-" + identity, kind = "inline", disposition = "publishable", name = "records.json", mediaType = "application/json",
                contentBase64 = Convert.ToBase64String(content), digest = "sha256:" + digest,
                dataContract = records.CollectionContract, recordContract = contract,
                provenance = new Dictionary<string, string> { ["inputArtifactId"] = parentId, ["inputSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes.Span)),
                    ["recordSelectionPath"] = path, ["transformation"] = "json.explode@1.0.0" }
            });
        }
        var result = JsonSerializer.SerializeToElement(new { artifacts = output });
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > 512 * 1024) throw Error("function_json_records_limit");
        return result;
    }

    private static JsonRecordProjection Project(JsonElement value, string path, ResolvedDataContract contract, CancellationToken ct)
    {
        var selected = FunctionJsonPointer.Select(value, path);
        if (selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() is < 1 or > JsonRecordProjection.MaximumRecords)
            throw Error("function_json_records_invalid");
        var result = new JsonRecordProjection { Value = selected.Clone(), RecordContract = contract, SelectionPath = path };
        try { result.Validate(ct); }
        catch (ArgumentException) { throw Error("function_json_records_invalid"); }
        return result;
    }
    private static JsonRecordProjection ProjectRecords(JsonElement records, string path, ResolvedDataContract contract, CancellationToken ct)
    {
        if (records.ValueKind != JsonValueKind.Array) throw Error("function_json_records_invalid");
        var output = new List<JsonElement>();
        foreach (var record in records.EnumerateArray())
        {
            var selected = Project(record, path, contract, ct);
            output.AddRange(selected.Value.EnumerateArray());
            if (output.Count > JsonRecordProjection.MaximumRecords) throw Error("function_json_records_limit");
        }
        var result = new JsonRecordProjection { Value = JsonSerializer.SerializeToElement(output), RecordContract = contract, SelectionPath = path };
        result.Validate(ct);
        return result;
    }
    private static TransformationFunctionException Error(string code) => new(code, "Select a non-empty bounded array of JSON objects and an exact compatible item schema.");
}
