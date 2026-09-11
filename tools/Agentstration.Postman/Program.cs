using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentstration.Web.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Postman;

internal static partial class PostmanProgram
{
    private const string CollectionRelativePath = "dev/postman/Agentstration.postman_collection.json";
    private const string LocalEnvironmentRelativePath = "dev/postman/environments/Agentstration.Local.postman_environment.json";
    private const string DockerEnvironmentRelativePath = "dev/postman/environments/Agentstration.Docker.postman_environment.json";
    private const string ScenarioRelativePath = "dev/postman/scenarios/SourceRegistries.postman_scenario.json";

    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete", "head", "options", "trace"];
    private static readonly string[] PreferredTagOrder =
    [
        "System", "Authentication", "Identity", "AEP enrollment", "Bootstrap", "Resource scopes",
        "Management", "Flows", "Runtime", "Work", "Work operations", "Workplace", "Model management",
        "Extensions", "Source providers", "Sources", "Source registries", "Packs", "Tools", "Tool governance",
        "Secrets", "Triggers", "Diagnostics", "API"
    ];

    public static async Task<int> Main(string[] args)
    {
        var check = args.Contains("--check", StringComparer.OrdinalIgnoreCase);
        var repositoryRoot = FindRepositoryRoot();
        var collectionPath = Path.Combine(repositoryRoot, CollectionRelativePath);
        var localEnvironmentPath = Path.Combine(repositoryRoot, LocalEnvironmentRelativePath);
        var dockerEnvironmentPath = Path.Combine(repositoryRoot, DockerEnvironmentRelativePath);
        var scenarioPath = Path.Combine(repositoryRoot, ScenarioRelativePath);

        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var openApi = await client.GetStringAsync(OpenApiConfiguration.DocumentPath);
        using var document = JsonDocument.Parse(openApi);

        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [collectionPath] = Serialize(CreateCollection(document.RootElement, scenarioPath)),
            [localEnvironmentPath] = Serialize(CreateEnvironment("Agentstration - Local", "3e4663ef-b619-49d6-bbfd-a4ac7aa93019", "localhost", "5100")),
            [dockerEnvironmentPath] = Serialize(CreateEnvironment("Agentstration - Docker", "ee9e50af-3fc4-40db-9184-f2df82ba10bf", "localhost", "5100"))
        };

        var stale = artifacts.Where(pair => !File.Exists(pair.Key) || !SameContent(File.ReadAllText(pair.Key), pair.Value)).ToArray();
        if (check)
        {
            if (stale.Length == 0)
            {
                Console.WriteLine("Postman artifacts are up to date.");
                return 0;
            }

            Console.Error.WriteLine("Postman artifacts are stale. Run: dotnet run --project tools/Agentstration.Postman");
            foreach (var pair in stale) Console.Error.WriteLine($"- {Path.GetRelativePath(repositoryRoot, pair.Key)}");
            return 1;
        }

        foreach (var pair in artifacts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
            await File.WriteAllTextAsync(pair.Key, pair.Value);
            Console.WriteLine($"Updated {Path.GetRelativePath(repositoryRoot, pair.Key)}");
        }

        return 0;
    }

    private static JsonObject CreateCollection(JsonElement openApi, string scenarioPath)
    {
        var parameterNames = new SortedSet<string>(StringComparer.Ordinal);
        var folders = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var paths = openApi.GetProperty("paths");

        foreach (var pathProperty in paths.EnumerateObject().OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            foreach (var operationProperty in pathProperty.Value.EnumerateObject()
                         .Where(value => HttpMethods.Contains(value.Name, StringComparer.Ordinal))
                         .OrderBy(value => Array.IndexOf(HttpMethods, value.Name)))
            {
                var operation = operationProperty.Value;
                var tag = operation.TryGetProperty("tags", out var tags) && tags.GetArrayLength() > 0
                    ? tags[0].GetString() ?? "API"
                    : "API";
                if (!folders.TryGetValue(tag, out var requests))
                {
                    requests = [];
                    folders[tag] = requests;
                }

                requests.Add(CreateRequest(
                    openApi,
                    operationProperty.Name.ToUpperInvariant(),
                    pathProperty.Name,
                    pathProperty.Value,
                    operation,
                    parameterNames));
            }
        }

        var referenceFolders = new JsonArray();
        foreach (var tag in folders.Keys.OrderBy(TagOrder).ThenBy(value => value, StringComparer.Ordinal))
            referenceFolders.Add(new JsonObject { ["name"] = tag, ["item"] = folders[tag] });

        var scenario = JsonNode.Parse(File.ReadAllText(scenarioPath))?.AsObject()
            ?? throw new InvalidOperationException($"Invalid Postman scenario file: {scenarioPath}");
        var scenarioItems = scenario["item"]?.DeepClone() ?? new JsonArray();

        var variables = BaseVariables();
        MergeScenarioVariables(variables, scenario);
        foreach (var parameterName in parameterNames)
        {
            if (variables.Any(node => string.Equals(node?["key"]?.GetValue<string>(), parameterName, StringComparison.Ordinal))) continue;
            variables.Add(Variable(parameterName, DefaultParameterValue(parameterName)));
        }

        return new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["_postman_id"] = "286ad89f-2ce7-4a63-9ed0-fcb6078864ba",
                ["name"] = "Agentstration HTTP API",
                ["description"] = "Generated from Agentstration's authoritative OpenAPI document. API Reference is regenerated; Scenarios are curated. SignalR and MCP are separate transports. See dev/postman/README.md.",
                ["schema"] = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            },
            ["event"] = new JsonArray
            {
                new JsonObject
                {
                    ["listen"] = "prerequest",
                    ["script"] = Script(
                        "const token = pm.environment.get('bearerToken') || pm.collectionVariables.get('bearerToken');",
                        "if (token) pm.request.headers.upsert({ key: 'Authorization', value: `Bearer ${token}` });",
                        "else pm.request.headers.remove('Authorization');")
                }
            },
            ["variable"] = variables,
            ["item"] = new JsonArray
            {
                new JsonObject { ["name"] = "API Reference", ["item"] = referenceFolders },
                new JsonObject { ["name"] = "Scenarios", ["description"] = "Curated requests intended to be run in folder order.", ["item"] = scenarioItems }
            }
        };
    }

    private static JsonObject CreateRequest(
        JsonElement openApi,
        string method,
        string path,
        JsonElement pathItem,
        JsonElement operation,
        ISet<string> parameterNames)
    {
        var parameters = Parameters(pathItem, operation).ToArray();
        foreach (var parameter in parameters)
        {
            if (parameter.TryGetProperty("name", out var parameterName) && parameterName.GetString() is { Length: > 0 } value)
                parameterNames.Add(value);
        }

        var name = operation.TryGetProperty("summary", out var summary) && !string.IsNullOrWhiteSpace(summary.GetString())
            ? summary.GetString()!
            : operation.TryGetProperty("operationId", out var operationId)
                ? operationId.GetString() ?? $"{method} {path}"
                : $"{method} {path}";
        var description = BuildDescription(method, path, operation);
        var headers = new JsonArray { new JsonObject { ["key"] = "Accept", ["value"] = PreferredResponseMediaType(operation) } };
        var request = new JsonObject
        {
            ["method"] = method,
            ["header"] = headers,
            ["url"] = CreateUrl(path, parameters),
            ["description"] = description
        };

        if (operation.TryGetProperty("requestBody", out var requestBody))
        {
            var body = CreateBody(openApi, requestBody, headers);
            if (body is not null) request["body"] = body;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["request"] = request,
            ["response"] = new JsonArray()
        };
    }

    private static IEnumerable<JsonElement> Parameters(JsonElement pathItem, JsonElement operation)
    {
        if (pathItem.TryGetProperty("parameters", out var pathParameters))
            foreach (var parameter in pathParameters.EnumerateArray()) yield return parameter;
        if (operation.TryGetProperty("parameters", out var operationParameters))
            foreach (var parameter in operationParameters.EnumerateArray()) yield return parameter;
    }

    private static JsonObject CreateUrl(string path, IReadOnlyCollection<JsonElement> parameters)
    {
        var postmanPath = PathParameterRegex().Replace(path, "{{$1}}");
        var query = new JsonArray();
        foreach (var parameter in parameters.Where(value => value.GetProperty("in").GetString() == "query"))
        {
            var name = parameter.GetProperty("name").GetString()!;
            var required = parameter.TryGetProperty("required", out var requiredValue) && requiredValue.GetBoolean();
            query.Add(new JsonObject
            {
                ["key"] = name,
                ["value"] = $"{{{{{name}}}}}",
                ["description"] = parameter.TryGetProperty("description", out var description) ? description.GetString() : null,
                ["disabled"] = !required
            });
        }

        var rawQuery = query.Count == 0
            ? string.Empty
            : "?" + string.Join("&", query.Select(value => $"{value!["key"]!.GetValue<string>()}={value["value"]!.GetValue<string>()}"));
        var url = new JsonObject
        {
            ["raw"] = $"{{{{baseUrl}}}}{postmanPath}{rawQuery}",
            ["host"] = new JsonArray("{{baseUrl}}"),
            ["path"] = new JsonArray(postmanPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
        };
        if (query.Count > 0) url["query"] = query;
        return url;
    }

    private static JsonObject? CreateBody(JsonElement openApi, JsonElement requestBody, JsonArray headers)
    {
        if (!requestBody.TryGetProperty("content", out var content) || content.GetRawText() == "{}") return null;
        var media = content.EnumerateObject().OrderBy(MediaTypeOrder).First();
        if (media.Name.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            headers.Add(new JsonObject { ["key"] = "Content-Type", ["value"] = media.Name, ["disabled"] = true });
            var formData = new JsonArray();
            if (media.Value.TryGetProperty("schema", out var schema) && schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    var binary = property.Value.TryGetProperty("format", out var format) && format.GetString() == "binary";
                    formData.Add(new JsonObject
                    {
                        ["key"] = property.Name,
                        ["type"] = binary ? "file" : "text",
                        ["src"] = binary ? new JsonArray() : null,
                        ["value"] = binary ? null : string.Empty
                    });
                }
            }
            return new JsonObject { ["mode"] = "formdata", ["formdata"] = formData };
        }

        if (media.Name.Equals("application/zip", StringComparison.OrdinalIgnoreCase)
            || media.Name.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            headers.Add(new JsonObject { ["key"] = "Content-Type", ["value"] = media.Name });
            return new JsonObject { ["mode"] = "file", ["file"] = new JsonObject { ["src"] = string.Empty } };
        }

        headers.Add(new JsonObject { ["key"] = "Content-Type", ["value"] = media.Name });
        var example = media.Value.TryGetProperty("schema", out var bodySchema)
            ? Example(openApi, bodySchema, 0, [])
            : null;
        return new JsonObject
        {
            ["mode"] = "raw",
            ["raw"] = example?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}",
            ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = "json" } }
        };
    }

    private static JsonNode? Example(JsonElement openApi, JsonElement schema, int depth, HashSet<string> references)
    {
        if (depth > 8) return null;
        if (schema.TryGetProperty("example", out var explicitExample)) return JsonNode.Parse(explicitExample.GetRawText());
        if (schema.TryGetProperty("default", out var defaultValue)) return JsonNode.Parse(defaultValue.GetRawText());
        if (schema.TryGetProperty("enum", out var values) && values.GetArrayLength() > 0) return JsonNode.Parse(values[0].GetRawText());
        if (schema.TryGetProperty("$ref", out var referenceValue))
        {
            var reference = referenceValue.GetString()!;
            if (!references.Add(reference)) return null;
            var resolved = ResolveReference(openApi, reference);
            var value = Example(openApi, resolved, depth + 1, references);
            references.Remove(reference);
            return value;
        }
        if (schema.TryGetProperty("allOf", out var allOf))
        {
            var merged = new JsonObject();
            foreach (var candidate in allOf.EnumerateArray())
                if (Example(openApi, candidate, depth + 1, references) is JsonObject part)
                    foreach (var property in part) merged[property.Key] = property.Value?.DeepClone();
            return merged;
        }
        foreach (var keyword in new[] { "oneOf", "anyOf" })
            if (schema.TryGetProperty(keyword, out var choices) && choices.GetArrayLength() > 0)
                return Example(openApi, choices[0], depth + 1, references);

        var type = schema.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        if (type == "object" || schema.TryGetProperty("properties", out _))
        {
            var result = new JsonObject();
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    result[property.Name] = Example(openApi, property.Value, depth + 1, references);
            return result;
        }
        if (type == "array")
        {
            var array = new JsonArray();
            if (schema.TryGetProperty("items", out var items)) array.Add(Example(openApi, items, depth + 1, references));
            return array;
        }
        if (type == "boolean") return false;
        if (type == "integer" || type == "number") return 0;
        if (schema.TryGetProperty("format", out var formatValue))
        {
            return formatValue.GetString() switch
            {
                "uuid" => "00000000-0000-0000-0000-000000000000",
                "date-time" => "2026-01-01T00:00:00Z",
                "date" => "2026-01-01",
                "uri" => "https://example.invalid/",
                _ => string.Empty
            };
        }
        return string.Empty;
    }

    private static JsonElement ResolveReference(JsonElement openApi, string reference)
    {
        const string prefix = "#/components/schemas/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported OpenAPI reference: {reference}");
        var name = reference[prefix.Length..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return openApi.GetProperty("components").GetProperty("schemas").GetProperty(name);
    }

    private static string BuildDescription(string method, string path, JsonElement operation)
    {
        var lines = new List<string>();
        if (operation.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()))
            lines.Add(description.GetString()!);
        lines.Add(operation.TryGetProperty("security", out var security) && security.GetArrayLength() > 0
            ? "Authentication: local session cookie or Bearer JWT/Agentstration PAT."
            : "Authentication: anonymous.");
        if (method == "DELETE") lines.Add("Caution: destructive operation.");
        if (path.EndsWith("/events", StringComparison.OrdinalIgnoreCase)) lines.Add("This request returns a server-sent event stream and remains open while events are produced.");
        if (operation.TryGetProperty("responses", out var responses))
        {
            var outcomes = responses.EnumerateObject()
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .Select(value => $"{value.Name}: {(value.Value.TryGetProperty("description", out var responseDescription) ? responseDescription.GetString() : "Response")}");
            lines.Add("Responses: " + string.Join("; ", outcomes));
        }
        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static string PreferredResponseMediaType(JsonElement operation)
    {
        if (!operation.TryGetProperty("responses", out var responses)) return "application/json";
        foreach (var response in responses.EnumerateObject().OrderBy(value => value.Name, StringComparer.Ordinal))
            if (response.Value.TryGetProperty("content", out var content))
                return content.EnumerateObject().Select(value => value.Name).FirstOrDefault() ?? "application/json";
        return "application/json";
    }

    private static int MediaTypeOrder(JsonProperty media) => media.Name switch
    {
        "application/json" => 0,
        "multipart/form-data" => 1,
        "application/zip" => 2,
        "application/octet-stream" => 3,
        _ when media.Name.EndsWith("+json", StringComparison.OrdinalIgnoreCase) => 0,
        _ => 10
    };

    private static JsonArray BaseVariables() =>
    [
        Variable("scheme", "http"),
        Variable("host", "localhost"),
        Variable("port", "5100"),
        Variable("baseUrl", "{{scheme}}://{{host}}:{{port}}"),
        Variable("userName", "admin"),
        Variable("password", string.Empty),
        Variable("bearerToken", string.Empty),
        Variable("namespace", "default"),
        Variable("workspaceName", "default"),
        Variable("skip", "0"),
        Variable("take", "50")
    ];

    private static void MergeScenarioVariables(JsonArray target, JsonObject scenario)
    {
        if (scenario["variable"] is not JsonArray variables) return;
        var existing = target.Select(node => node?["key"]?.GetValue<string>()).Where(value => value is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            var key = variable?["key"]?.GetValue<string>();
            if (key is null || existing.Contains(key) || key is "baseUrl" or "userName" or "password" or "bearerToken") continue;
            target.Add(variable!.DeepClone());
            existing.Add(key);
        }
    }

    private static JsonObject Variable(string key, string value) => new() { ["key"] = key, ["value"] = value, ["type"] = "string" };

    private static string DefaultParameterValue(string name) => name switch
    {
        "namespace" => "default",
        "workspaceName" => "default",
        "skip" => "0",
        "take" or "limit" => "50",
        _ => string.Empty
    };

    private static JsonObject CreateEnvironment(string name, string id, string host, string port) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["values"] = new JsonArray
        {
            EnvironmentValue("scheme", "http", true),
            EnvironmentValue("host", host, true),
            EnvironmentValue("port", port, true),
            EnvironmentValue("userName", "admin", true),
            EnvironmentValue("password", string.Empty, true),
            EnvironmentValue("bearerToken", string.Empty, true),
            EnvironmentValue("namespace", "default", true),
            EnvironmentValue("workspaceName", "default", true)
        },
        ["_postman_variable_scope"] = "environment",
        ["_postman_exported_at"] = "2026-09-11T00:00:00.000Z",
        ["_postman_exported_using"] = "Agentstration.Postman",
        ["schema"] = "https://schema.getpostman.com/json/environment/v2.1.0/environment.json"
    };

    private static JsonObject EnvironmentValue(string key, string value, bool enabled) =>
        new() { ["key"] = key, ["value"] = value, ["type"] = "default", ["enabled"] = enabled };

    private static JsonObject Script(params string[] lines) =>
        new() { ["type"] = "text/javascript", ["exec"] = new JsonArray(lines.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };

    private static int TagOrder(string tag)
    {
        var index = Array.IndexOf(PreferredTagOrder, tag);
        return index >= 0 ? index : PreferredTagOrder.Length;
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    private static bool SameContent(string current, string expected) =>
        string.Equals(current.Replace("\r\n", "\n", StringComparison.Ordinal), expected.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Agentstration repository root.");
    }

    [GeneratedRegex("\\{([^}:]+)(?::[^}]+)?\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PathParameterRegex();
}
