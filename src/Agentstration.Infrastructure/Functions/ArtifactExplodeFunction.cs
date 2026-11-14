using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Builtins;
using Agentstration.Identity.Contracts;
using Agentstration.Schemas;

namespace Agentstration.Infrastructure.Functions;

/// <summary>Expands exact readable inputs into an inline manifest; source publication owns final materialization.</summary>
public sealed class ArtifactExplodeFunction(ArtifactManagementService artifacts, IArtifactDurableStore durable,
    ICurrentRequestContext current, IRequestContextScopeFactory scopes, INativeFunctionExecutionContext execution) : INativeTransformationFunction
{
    public NativeFunctionDescriptor Descriptor { get; } = FunctionSchemas.Descriptor("artifact.explode", """
        {"value":{"anyOf":[{"type":"object","properties":{"artifacts":{"type":"array","maxItems":128}},"required":["artifacts"]},{"type":"object","properties":{"content":{}},"required":["content"],"additionalProperties":false}]},"path":{"type":"string","maxLength":1024},"mode":{"type":"string","enum":["arrayItems","objectValues"],"default":"arrayItems"},"itemSchema":{"type":"object"},"maximumOutputs":{"type":"integer","minimum":1,"maximum":100,"default":100}}
        """, ["value", "path"], """{"type":"object","properties":{"artifacts":{"type":"array","items":{"type":"object","properties":{"artifactId":{"type":"string"}},"required":["artifactId"]}}},"required":["artifacts"],"additionalProperties":false}""");

    public async ValueTask<JsonElement> ExecuteAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try { return await ExplodeAsync(input, cancellationToken); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException)
        { throw Error("function_artifact_invalid"); }
    }

    private async ValueTask<JsonElement> ExplodeAsync(JsonElement input, CancellationToken cancellationToken)
    {
        // Content belongs to the trusted operational Workspace, including administrative source execution.
        using var operational = scopes.Push(current.Current);
        var path = input.GetProperty("path").GetString()!;
        var mode = input.TryGetProperty("mode", out var selectedMode) ? selectedMode.GetString() : "arrayItems";
        var maximum = input.TryGetProperty("maximumOutputs", out var selectedMaximum) ? selectedMaximum.GetInt32() : 100;
        var contract = input.TryGetProperty("itemSchema", out var itemSchema) ? DataContracts.Declared(itemSchema, "function:artifact.explode/item", "application/json") : null;
        var results = new List<JsonElement>();
        var totalBytes = 0;
        var source = input.GetProperty("value");
        var sources = source.TryGetProperty("artifacts", out var sourceArtifacts) ? sourceArtifacts.EnumerateArray().ToArray()
            : source.TryGetProperty("content", out var sourceContent) ? new[] { InlineSource(sourceContent) } : throw Error("function_artifact_invalid");
        foreach (var item in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentId = item.GetProperty("artifactId").GetString() ?? throw Error("function_artifact_invalid");
            var bytes = await ReadAsync(item, parentId, cancellationToken);
            var parentDigest = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
            if (item.TryGetProperty("digest", out var declared) && declared.ValueKind == JsonValueKind.String
                && !string.Equals(declared.GetString()!.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), parentDigest, StringComparison.OrdinalIgnoreCase))
                throw Error("function_artifact_integrity");
            using var json = JsonDocument.Parse(bytes);
            var selected = FunctionJsonPointer.Select(json.RootElement, path);
            if (mode == "arrayItems" && selected.ValueKind != JsonValueKind.Array
                || mode == "objectValues" && selected.ValueKind != JsonValueKind.Object) throw Error("function_explode_collection_required");
            var values = mode == "arrayItems"
                ? selected.EnumerateArray().Select((value, index) => (Key: index.ToString(System.Globalization.CultureInfo.InvariantCulture), Value: value)).ToArray()
                : selected.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => (Key: property.Name, Value: property.Value)).ToArray();
            if (values.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != values.Length) throw Error("function_explode_duplicate_key");
            foreach (var (key, value) in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (results.Count >= maximum) throw Error("function_explode_limit");
                DataContracts.Validate(contract, value, "function-explode-item");
                var content = JsonSerializer.SerializeToUtf8Bytes(value);
                totalBytes = checked(totalBytes + content.Length);
                if (content.Length > 65536 || totalBytes > 512 * 1024) throw Error("function_explode_limit");
                var identity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { invocation = execution.InvocationId, parentId, parentDigest, path, mode, key, version = Descriptor.Version })));
                results.Add(JsonSerializer.SerializeToElement(new
                {
                    artifactId = "exploded-" + identity, kind = "inline", disposition = "publishable", name = "item-" + results.Count + ".json",
                    mediaType = "application/json", digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content)),
                    contentBase64 = Convert.ToBase64String(content), dataContract = contract is null ? null : contract with { Validated = true },
                    provenance = new Dictionary<string, string> { ["inputArtifactId"] = parentId, ["inputSha256"] = parentDigest,
                        ["expansionPath"] = path, ["expansionMode"] = mode!, ["expansionKey"] = key, ["transformation"] = Descriptor.Id + "@" + Descriptor.Version }
                }));
            }
        }
        if (results.Count == 0) throw Error("function_explode_empty");
        if (results.Select(value => value.GetProperty("artifactId").GetString()).Distinct(StringComparer.Ordinal).Count() != results.Count)
            throw Error("function_explode_duplicate_input");
        return JsonSerializer.SerializeToElement(new { artifacts = results });
    }

    private static JsonElement InlineSource(JsonElement value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        return JsonSerializer.SerializeToElement(new { artifactId = "inline-" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
            kind = "inline", contentBase64 = Convert.ToBase64String(bytes) });
    }

    private async Task<ReadOnlyMemory<byte>> ReadAsync(JsonElement item, string id, CancellationToken ct)
    {
        var kind = item.TryGetProperty("kind", out var selectedKind) ? selectedKind.GetString() : "durable";
        ReadOnlyMemory<byte> bytes;
        if (kind == "inline")
        {
            var encoded = item.GetProperty("contentBase64").GetString();
            if (encoded is null || encoded.Length > 1_398_104) throw Error("function_artifact_limit");
            bytes = Convert.FromBase64String(encoded);
        }
        else if (kind == "staged")
        {
            var stage = (await artifacts.GetStagedAsync(StagedArtifactId.Parse(id), null, ArtifactLeaseOperation.Read, ct))?.Value
                ?? throw Error("function_artifact_unavailable");
            if (stage.ArtifactStatus is not (StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted) || stage.Length > 1024 * 1024)
                throw Error("function_artifact_invalid");
            using var content = new MemoryStream();
            while (content.Length < stage.Length)
            {
                var part = Convert.FromBase64String((await artifacts.ReadAsync(stage.ArtifactId, content.Length, (int)Math.Min(65536, stage.Length - content.Length), null, ct)).ContentBase64);
                if (part.Length == 0) throw Error("function_artifact_incomplete");
                await content.WriteAsync(part, ct);
            }
            bytes = content.ToArray();
            if (bytes.Length != stage.Length) throw Error("function_artifact_incomplete");
        }
        else if (kind == "durable")
        {
            var value = (await artifacts.GetFlowRunArtifactForContentReadAsync(FlowRunArtifactId.Parse(id), true, ct))?.Value
                ?? throw Error("function_artifact_unavailable");
            if (value.Receipt.Length > 1024 * 1024) throw Error("function_artifact_limit");
            using var content = new MemoryStream();
            while (content.Length < value.Receipt.Length)
            {
                var part = await durable.ReadAsync(new(current.Current.WorkspaceId), value.Receipt.OpaqueReference,
                    content.Length, (int)Math.Min(65536, value.Receipt.Length - content.Length), ct);
                if (part.Length == 0) throw Error("function_artifact_incomplete");
                await content.WriteAsync(part, ct);
            }
            bytes = content.ToArray();
            if (bytes.Length != value.Receipt.Length || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), value.Receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Error("function_artifact_integrity");
        }
        else throw Error("function_artifact_invalid");
        if (bytes.Length > 1024 * 1024) throw Error("function_artifact_limit");
        return bytes;
    }
    private static TransformationFunctionException Error(string code) => new(code, "Artifact expansion requires exact readable JSON content, a valid collection path and bounded unique outputs.");
}
