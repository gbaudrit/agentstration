using System.Text.Json;
using Agentstration.Web.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PostmanCollectionTests
{
    [TestMethod]
    public async Task ApiReferenceMatchesRegisteredAndOpenApiOperations()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var openApi = JsonDocument.Parse(await client.GetStringAsync(OpenApiConfiguration.DocumentPath));
        using var postman = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("dev", "postman", "Agentstration.postman_collection.json")));

        var registered = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => IsApiRoute(endpoint.RoutePattern.RawText))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Select(method => Key(method, NormalizeRoute(endpoint.RoutePattern.RawText!))) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var documented = OpenApiOperations(openApi.RootElement);
        var collected = PostmanReferenceOperations(postman.RootElement);

        AssertSetsEqual(registered, documented, "registered endpoints", "OpenAPI");
        AssertSetsEqual(documented, collected, "OpenAPI", "Postman API Reference");
    }

    [TestMethod]
    public void CollectionUsesComposableEndpointVariablesAndCommittedEnvironmentsContainNoSecrets()
    {
        using var collection = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("dev", "postman", "Agentstration.postman_collection.json")));
        var variables = collection.RootElement.GetProperty("variable").EnumerateArray()
            .ToDictionary(value => value.GetProperty("key").GetString()!, value => value.GetProperty("value").GetString(), StringComparer.Ordinal);

        Assert.AreEqual("http", variables["scheme"]);
        Assert.AreEqual("localhost", variables["host"]);
        Assert.AreEqual("5100", variables["port"]);
        Assert.AreEqual("{{scheme}}://{{host}}:{{port}}", variables["baseUrl"]);
        Assert.AreEqual(string.Empty, variables["password"]);
        Assert.AreEqual(string.Empty, variables["bearerToken"]);

        foreach (var environmentName in new[] { "Agentstration.Local.postman_environment.json", "Agentstration.Docker.postman_environment.json" })
        {
            using var environment = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("dev", "postman", "environments", environmentName)));
            var values = environment.RootElement.GetProperty("values").EnumerateArray()
                .ToDictionary(value => value.GetProperty("key").GetString()!, value => value.GetProperty("value").GetString(), StringComparer.Ordinal);
            Assert.IsTrue(values.ContainsKey("scheme"));
            Assert.IsTrue(values.ContainsKey("host"));
            Assert.IsTrue(values.ContainsKey("port"));
            Assert.AreEqual(string.Empty, values["password"]);
            Assert.AreEqual(string.Empty, values["bearerToken"]);
        }
    }

    [TestMethod]
    public void ApiReferenceRequestsUseBaseUrlAndHaveNoDuplicateOperation()
    {
        using var collection = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("dev", "postman", "Agentstration.postman_collection.json")));
        var requests = ReferenceRequests(collection.RootElement).ToArray();
        var operations = requests.Select(RequestKey).ToArray();

        Assert.IsTrue(requests.All(request => RawUrl(request).StartsWith("{{baseUrl}}/", StringComparison.Ordinal)));
        Assert.HasCount(operations.Length, operations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [TestMethod]
    public async Task SpecializedApiDomainsDoNotUseTheGenericTag()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(OpenApiConfiguration.DocumentPath));
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/aep/enrollments"] = "AEP enrollment",
            ["/api/bootstrap/profiles"] = "Bootstrap",
            ["/api/resource-scopes"] = "Resource scopes",
            ["/api/resources"] = "Resources",
            ["/api/entries"] = "Workplace",
            ["/api/extensionregistrations"] = "Extensions",
            ["/api/sourceproviders"] = "Source providers",
            ["/api/sourceregistries"] = "Source registries",
            ["/api/sources"] = "Sources",
            ["/api/tooldefinitions"] = "Tools",
            ["/health/ready"] = "System"
        };

        foreach (var pair in expected)
        {
            var operation = document.RootElement.GetProperty("paths").GetProperty(pair.Key).EnumerateObject()
                .First(value => IsHttpMethod(value.Name)).Value;
            Assert.AreEqual(pair.Value, operation.GetProperty("tags")[0].GetString(), pair.Key);
        }
    }

    private static WebApplicationFactory<global::Program> CreateFactory() =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

    private static HashSet<string> OpenApiOperations(JsonElement root) => root.GetProperty("paths")
        .EnumerateObject()
        .Where(path => IsApiRoute(path.Name))
        .SelectMany(path => path.Value.EnumerateObject()
            .Where(operation => IsHttpMethod(operation.Name))
            .Select(operation => Key(operation.Name, path.Name)))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> PostmanReferenceOperations(JsonElement root) => ReferenceRequests(root)
        .Select(RequestKey)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<JsonElement> ReferenceRequests(JsonElement root)
    {
        var reference = root.GetProperty("item").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "API Reference");
        foreach (var folder in reference.GetProperty("item").EnumerateArray())
            foreach (var request in folder.GetProperty("item").EnumerateArray())
                yield return request;
    }

    private static string RequestKey(JsonElement item)
    {
        var request = item.GetProperty("request");
        var method = request.GetProperty("method").GetString()!;
        var raw = RawUrl(request);
        const string baseUrl = "{{baseUrl}}";
        Assert.IsTrue(raw.StartsWith(baseUrl, StringComparison.Ordinal));
        var path = raw[baseUrl.Length..];
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0) path = path[..queryIndex];
        return Key(method, NormalizePostmanRoute(path));
    }

    private static string RawUrl(JsonElement request)
    {
        var url = request.GetProperty("url");
        return url.ValueKind == JsonValueKind.String ? url.GetString()! : url.GetProperty("raw").GetString()!;
    }

    private static string NormalizePostmanRoute(string route)
    {
        var value = route;
        var start = value.IndexOf("{{", StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0) break;
            value = value[..start] + "{" + value[(start + 2)..end] + "}" + value[(end + 2)..];
            start = value.IndexOf("{{", start + 1, StringComparison.Ordinal);
        }
        return NormalizeRoute(value);
    }

    private static string NormalizeRoute(string route)
    {
        var input = "/" + route.Trim('/');
        var output = new System.Text.StringBuilder(input.Length);
        var inParameter = false;
        var inConstraint = false;
        foreach (var character in input)
        {
            if (character == '{') inParameter = true;
            if (inParameter && character == ':') inConstraint = true;
            if (!inConstraint) output.Append(character);
            if (character != '}') continue;
            if (inConstraint) output.Append(character);
            inParameter = false;
            inConstraint = false;
        }
        return output.Length > 1 ? output.ToString().TrimEnd('/') : output.ToString();
    }

    private static string Key(string method, string route) => $"{method.ToUpperInvariant()} {NormalizeRoute(route)}";

    private static bool IsApiRoute(string? route) => route is not null
        && (route.Equals("health", StringComparison.OrdinalIgnoreCase)
            || route.StartsWith("health/", StringComparison.OrdinalIgnoreCase)
            || route.StartsWith("/health/", StringComparison.OrdinalIgnoreCase)
            || route.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            || route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase));

    private static bool IsHttpMethod(string method) => method is "get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "trace";

    private static void AssertSetsEqual(HashSet<string> expected, HashSet<string> actual, string expectedName, string actualName)
    {
        var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var stale = actual.Except(expected, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        Assert.IsEmpty(missing, $"Operations present in {expectedName} but missing from {actualName}:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
        Assert.IsEmpty(stale, $"Operations present in {actualName} but missing from {expectedName}:{Environment.NewLine}{string.Join(Environment.NewLine, stale)}");
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"Could not locate repository file: {Path.Combine(segments)}");
    }
}
