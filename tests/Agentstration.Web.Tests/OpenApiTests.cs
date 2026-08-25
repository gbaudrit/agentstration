using System.Net;
using System.Text.Json;
using Agentstration.Web.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class OpenApiTests
{
    [TestMethod]
    public async Task OpenApiDocumentCoversEveryRegisteredHttpApiRoute()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(OpenApiConfiguration.DocumentPath));

        var documented = document.RootElement.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => IsHttpMethod(operation.Name))
                .Select(operation => $"{operation.Name.ToUpperInvariant()} {path.Name}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => IsApiRoute(endpoint.RoutePattern.RawText))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Select(method => $"{method.ToUpperInvariant()} /{endpoint.RoutePattern.RawText!.TrimStart('/')}") ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = registered.Except(documented, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        Assert.IsEmpty(missing, $"Routes missing from OpenAPI:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    [TestMethod]
    public async Task OpenApiDocumentHasStableOperationsTagsAndSecurityMetadata()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(OpenApiConfiguration.DocumentPath));
        var root = document.RootElement;

        Assert.AreEqual("Agentstration HTTP API", root.GetProperty("info").GetProperty("title").GetString());
        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.IsTrue(schemes.TryGetProperty("Bearer", out _));
        Assert.IsTrue(schemes.TryGetProperty("ApplicationCookie", out _));

        var operations = root.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Where(operation => IsHttpMethod(operation.Name)))
            .Select(operation => operation.Value)
            .ToArray();
        var operationIds = operations.Select(operation => operation.GetProperty("operationId").GetString()).ToArray();
        Assert.IsTrue(operationIds.All(id => !string.IsNullOrWhiteSpace(id)));
        Assert.HasCount(operationIds.Length, operationIds.Distinct(StringComparer.Ordinal).ToArray());
        Assert.IsTrue(operations.All(operation => operation.GetProperty("tags").GetArrayLength() > 0));

        var declaredTags = root.GetProperty("tags")
            .EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString())
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
        var referencedTags = operations
            .SelectMany(operation => operation.GetProperty("tags").EnumerateArray())
            .Select(tag => tag.GetString())
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsEmpty(declaredTags.Except(referencedTags, StringComparer.Ordinal).ToArray(),
            "OpenAPI declares tags that are not referenced by any operation, which creates empty Swagger UI sections.");

        var protectedOperation = root.GetProperty("paths").GetProperty("/api/runtime/runs/{runId}").GetProperty("get");
        Assert.IsTrue(protectedOperation.TryGetProperty("security", out var security) && security.GetArrayLength() == 2);
        Assert.IsTrue(protectedOperation.GetProperty("responses").TryGetProperty("401", out _));
        Assert.IsTrue(protectedOperation.GetProperty("responses").TryGetProperty("403", out _));

        var anonymousOperation = root.GetProperty("paths").GetProperty("/api/auth/bootstrap").GetProperty("get");
        Assert.IsFalse(anonymousOperation.TryGetProperty("security", out _));

        var packDownload = root.GetProperty("paths").GetProperty("/api/pack-projects/{projectId}/builds/{buildId}/download").GetProperty("get");
        Assert.IsTrue(packDownload.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("application/zip", out _));

        var runtimeEvents = root.GetProperty("paths").GetProperty("/api/runtime/runs/{runId}/events").GetProperty("get");
        Assert.IsTrue(runtimeEvents.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("text/event-stream", out _));
    }

    [TestMethod]
    public async Task SwaggerUiUsesTheGeneratedOpenApiDocument()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync($"{OpenApiConfiguration.SwaggerPath}/index.html");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "Agentstration HTTP API");
        var initializer = await client.GetStringAsync($"{OpenApiConfiguration.SwaggerPath}/index.js");
        StringAssert.Contains(initializer, OpenApiConfiguration.DocumentPath);
    }

    [TestMethod]
    public async Task FlowOperationsExposeTheirActualSuccessContracts()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(OpenApiConfiguration.DocumentPath));
        var paths = document.RootElement.GetProperty("paths");

        var create = paths.GetProperty("/api/flows").GetProperty("post");
        Assert.AreEqual("Create a Flow", create.GetProperty("summary").GetString());
        AssertSchemaHasProperty(create, "201", "id");
        AssertSchemaHasProperty(create, "201", "definition");

        var list = paths.GetProperty("/api/flows").GetProperty("get");
        Assert.AreEqual("List Flows", list.GetProperty("summary").GetString());
        AssertSchemaHasProperty(list, "200", "value");
        AssertSchemaHasProperty(list, "200", "nextLink");

        var validate = paths.GetProperty("/api/flows/{id}/validate").GetProperty("post");
        Assert.AreEqual("Validate a Flow draft", validate.GetProperty("summary").GetString());
        AssertSchemaHasProperty(validate, "200", "isValid");
        AssertSchemaHasProperty(validate, "200", "issues");
    }

    [TestMethod]
    public async Task EveryApiOperationHasADocumentedSuccessOutcome()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(OpenApiConfiguration.DocumentPath));
        var gaps = document.RootElement.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => IsHttpMethod(operation.Name))
                .Select(operation => new
                {
                    Key = $"{operation.Name.ToUpperInvariant()} {path.Name}",
                    Operation = operation.Value
                }))
            .Where(item => item.Key.StartsWith("GET /api/", StringComparison.Ordinal)
                || item.Key.StartsWith("POST /api/", StringComparison.Ordinal)
                || item.Key.StartsWith("PUT /api/", StringComparison.Ordinal)
                || item.Key.StartsWith("DELETE /api/", StringComparison.Ordinal))
            .Where(item => !item.Operation.TryGetProperty("summary", out var summary)
                || string.IsNullOrWhiteSpace(summary.GetString())
                || !HasDocumentedSuccess(item.Operation))
            .Select(item => $"{item.Operation.GetProperty("tags")[0].GetString()}: {item.Key} => {DescribeResponses(item.Operation)}")
            .ToArray();

        Assert.IsEmpty(gaps, $"Operations without a typed success response ({gaps.Length}):{Environment.NewLine}{string.Join(Environment.NewLine, gaps)}");
    }

    private static WebApplicationFactory<global::Program> CreateFactory() =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

    private static bool IsApiRoute(string? route) => route is not null
        && (route.Equals("health", StringComparison.OrdinalIgnoreCase)
            || route.StartsWith("api/", StringComparison.OrdinalIgnoreCase));

    private static bool IsHttpMethod(string value) => value is "get" or "post" or "put" or "delete" or "patch" or "head" or "options" or "trace";

    private static bool HasDocumentedSuccess(JsonElement operation) => operation.GetProperty("responses")
        .EnumerateObject()
        .Where(response => response.Name.Length == 3 && response.Name[0] is '2' or '3')
        .Any(response => response.Name == "204"
            || response.Name[0] == '3'
            || response.Value.TryGetProperty("content", out var content) && content.EnumerateObject().Any());

    private static string DescribeResponses(JsonElement operation) => string.Join(", ", operation.GetProperty("responses")
        .EnumerateObject()
        .Select(response => $"{response.Name}:[{(response.Value.TryGetProperty("content", out var content) ? string.Join('|', content.EnumerateObject().Select(media => media.Name)) : string.Empty)}]"));

    private static void AssertSchemaHasProperty(JsonElement operation, string statusCode, string propertyName)
    {
        var schema = operation.GetProperty("responses")
            .GetProperty(statusCode)
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.IsTrue(schema.GetProperty("properties").TryGetProperty(propertyName, out _),
            $"Response {statusCode} does not expose schema property '{propertyName}'.");
    }
}
