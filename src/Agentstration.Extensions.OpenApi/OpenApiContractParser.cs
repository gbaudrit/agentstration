using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using YamlDotNet.Serialization;

namespace Agentstration.Extensions.OpenApi;

public sealed record ParsedOpenApiContract(OpenApiContractDescriptor Descriptor, JsonObject Document);

public sealed class OpenApiContractParser(IOptions<OpenApiOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 128 };
    private static readonly HashSet<string> Methods =
        new(["get", "put", "post", "delete", "options", "head", "patch", "trace"], StringComparer.OrdinalIgnoreCase);
    private readonly OpenApiOptions options = options.Value;

    public ParsedOpenApiContract Parse(string content, string contentType, Uri documentUri)
    {
        if (Encoding.UTF8.GetByteCount(content) > options.MaximumDocumentBytes)
            throw Error("openapi_document_too_large", $"OpenAPI documents cannot exceed {options.MaximumDocumentBytes} bytes.");
        var root = Normalize(content, contentType);
        var version = RequiredVersion(root);
        ValidateReferences(root);

        var read = OpenApiDocument.Parse(root.ToJsonString(), "json", new OpenApiReaderSettings { LoadExternalRefs = false });
        var parseIssues = (read.Diagnostic?.Errors ?? []).Select(error =>
            new OpenApiDiagnostic(error.Pointer ?? "$", "openapi_invalid", error.Message)).ToArray();
        if (parseIssues.Length > 0)
            throw Error("openapi_contract_invalid", string.Join(' ', parseIssues.Select(value => $"{value.Path}: {value.Message}")));

        var operations = ExtractOperations(root);
        if (operations.Count > options.MaximumOperations)
            throw Error("openapi_operations_exceeded", $"The contract contains more than {options.MaximumOperations} operations.");
        var duplicateIds = operations.Where(value => !string.IsNullOrWhiteSpace(value.OperationId))
            .GroupBy(value => value.OperationId!, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var finalized = operations.Select(value =>
        {
            var issues = value.Diagnostics.ToList();
            if (string.IsNullOrWhiteSpace(value.OperationId))
                issues.Add(new($"$.paths['{value.Path}'].{value.Method.ToLowerInvariant()}.operationId",
                    "openapi_operation_id_required", "A non-empty operationId is required for selection."));
            else if (duplicateIds.Contains(value.OperationId))
                issues.Add(new($"$.paths['{value.Path}'].{value.Method.ToLowerInvariant()}.operationId",
                    "openapi_operation_id_duplicate", $"operationId '{value.OperationId}' is not unique."));
            return value with { Eligible = issues.Count == 0, Diagnostics = issues };
        }).ToArray();

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var info = root["info"] as JsonObject;
        var descriptor = new OpenApiContractDescriptor(
            documentUri.AbsoluteUri, version, Text(info, "title"), Text(info, "description"), NormalizeMediaType(contentType),
            content, digest, ExtractServers(root, documentUri), finalized, parseIssues);
        return new(descriptor, root);
    }

    private static JsonObject Normalize(string content, string contentType)
    {
        try
        {
            JsonNode? node;
            if (NormalizeMediaType(contentType) == "application/json" || content.TrimStart().StartsWith('{'))
                node = JsonNode.Parse(content, documentOptions: new() { MaxDepth = 128 });
            else
            {
                var yaml = new DeserializerBuilder()
                    .WithAttemptingUnquotedStringTypeDeserialization()
                    .Build()
                    .Deserialize<object>(content);
                node = JsonSerializer.SerializeToNode(yaml, JsonOptions);
            }
            return node as JsonObject ?? throw Error("openapi_document_type", "An OpenAPI document must have an object root.");
        }
        catch (OpenApiExtensionException) { throw; }
        catch (Exception exception) when (exception is JsonException or YamlDotNet.Core.YamlException or InvalidOperationException)
        { throw Error("openapi_document_malformed", "The OpenAPI document is not valid JSON or YAML.", exception); }
    }

    private static string RequiredVersion(JsonObject root)
    {
        var version = Text(root, "openapi")?.Trim();
        if (version is null) throw Error("openapi_version_required", "The document must declare an OpenAPI version.");
        if (!version.StartsWith("3.0.", StringComparison.Ordinal) && !version.StartsWith("3.1.", StringComparison.Ordinal))
            throw Error("openapi_version_unsupported", $"OpenAPI version '{version}' is not supported.");
        return version;
    }

    private void ValidateReferences(JsonObject root)
    {
        var references = EnumerateReferences(root).ToArray();
        if (references.Length > options.MaximumReferences)
            throw Error("openapi_reference_count_exceeded", $"The contract contains more than {options.MaximumReferences} references.");
        foreach (var reference in references)
        {
            if (!reference.Value.StartsWith('#'))
            {
                if (Uri.TryCreate(reference.Value, UriKind.Absolute, out var external)
                    && !string.Equals(external.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(external.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    throw Error("openapi_reference_scheme_unsupported", $"Reference scheme '{external.Scheme}' is not supported.");
                throw Error("openapi_external_reference_denied", "External OpenAPI references are disabled by the initial bounded reference policy.");
            }
            if (ResolvePointer(root, reference.Value) is null)
                throw Error("openapi_reference_unresolved", $"Reference '{reference.Value}' cannot be resolved.");
        }

        foreach (var reference in references)
            Visit(reference.Value, root, [], 1);
    }

    private void Visit(string reference, JsonObject root, HashSet<string> chain, int depth)
    {
        if (depth > options.MaximumReferenceDepth)
            throw Error("openapi_reference_depth_exceeded", $"Reference depth cannot exceed {options.MaximumReferenceDepth}.");
        if (!chain.Add(reference)) throw Error("openapi_reference_cycle", $"Reference cycle detected at '{reference}'.");
        var target = ResolvePointer(root, reference)!;
        foreach (var nested in EnumerateReferences(target)) Visit(nested.Value, root, chain, depth + 1);
        chain.Remove(reference);
    }

    private static IEnumerable<(string Path, string Value)> EnumerateReferences(JsonNode node, string path = "$")
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                var childPath = $"{path}.{property.Key}";
                if (property.Key == "$ref" && property.Value is JsonValue value && value.TryGetValue<string>(out var reference))
                    yield return (childPath, reference);
                if (property.Value is not null)
                    foreach (var nested in EnumerateReferences(property.Value, childPath)) yield return nested;
            }
        }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                if (array[index] is { } child)
                    foreach (var nested in EnumerateReferences(child, $"{path}[{index}]")) yield return nested;
    }

    private static JsonNode? ResolvePointer(JsonObject root, string reference)
    {
        if (reference == "#") return root;
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        JsonNode? current = root;
        foreach (var encoded in reference[2..].Split('/'))
        {
            var segment = Uri.UnescapeDataString(encoded.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal));
            current = current switch
            {
                JsonObject obj when obj.TryGetPropertyValue(segment, out var value) => value,
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
            if (current is null) return null;
        }
        return current;
    }

    private static List<OpenApiOperationDescriptor> ExtractOperations(JsonObject root)
    {
        var result = new List<OpenApiOperationDescriptor>();
        if (root["paths"] is not JsonObject paths) return result;
        foreach (var path in paths)
        {
            if (path.Value is not JsonObject pathItem) continue;
            foreach (var method in pathItem.Where(value => Methods.Contains(value.Key)))
            {
                if (method.Value is not JsonObject operation) continue;
                var request = JsonSerializer.SerializeToElement(new
                {
                    parameters = CombineArrays(pathItem["parameters"], operation["parameters"]),
                    requestBody = operation["requestBody"]?.DeepClone()
                });
                JsonElement? response = operation["responses"] is null
                    ? null : JsonSerializer.SerializeToElement(operation["responses"]);
                result.Add(new(Text(operation, "operationId"), method.Key.ToUpperInvariant(), path.Key,
                    Text(operation, "summary") ?? Text(operation, "description"), false,
                    method.Key is "get" or "head" or "options" or "trace", request, response, []));
            }
        }
        return result;
    }

    private static JsonArray CombineArrays(JsonNode? first, JsonNode? second)
    {
        var result = new JsonArray();
        if (first is JsonArray firstArray) foreach (var item in firstArray) result.Add(item?.DeepClone());
        if (second is JsonArray secondArray) foreach (var item in secondArray) result.Add(item?.DeepClone());
        return result;
    }

    private static IReadOnlyList<string> ExtractServers(JsonObject root, Uri documentUri)
    {
        var result = new List<string>();
        if (root["servers"] is JsonArray servers)
            foreach (var value in servers.OfType<JsonObject>())
                if (Text(value, "url") is { } url && Uri.TryCreate(documentUri, url, out var resolved)) result.Add(resolved.AbsoluteUri);
        if (result.Count == 0) result.Add(new Uri(documentUri, "/").AbsoluteUri.TrimEnd('/'));
        return result.Distinct(StringComparer.Ordinal).Take(32).ToArray();
    }

    private static string? Text(JsonObject? value, string name) =>
        value?[name] is JsonValue text && text.TryGetValue<string>(out var result) ? result : null;

    private static string NormalizeMediaType(string value) => value.Split(';', 2)[0].Trim().ToLowerInvariant() switch
    {
        "application/vnd.oai.openapi+json" => "application/json",
        "application/vnd.oai.openapi" => "application/yaml",
        var mediaType => mediaType
    };

    private static OpenApiExtensionException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
}
