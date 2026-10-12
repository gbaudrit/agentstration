using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task SchemaClientPreservesExactScopeNamespaceAndConcurrencyToken()
    {
        var scope = ResourceScopeRef.Tenant(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var resource = Schema(scope, new ResourceNamespace("shared.contracts"));
        var requests = new List<(HttpMethod Method, string Path, string? IfMatch)>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Headers.IfMatch.SingleOrDefault()?.ToString()));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"schema-etag\"");
            return response;
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new SchemasApiClient(http);

        var current = await client.GetSchemaAsync(scope, resource.Namespace, resource.Name);
        _ = await client.UpdateAsync(scope, resource.Namespace, resource.Name,
            new PutSchemaRequest(resource.Definition), current.ETag);

        Assert.AreEqual("/api/schemas/contact?scopeRef=%2Ftenants%2Faaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&resourceNamespace=shared.contracts", requests[0].Path);
        Assert.AreEqual(requests[0].Path, requests[1].Path);
        Assert.AreEqual("\"schema-etag\"", requests[1].IfMatch);
    }

    [TestMethod]
    public async Task SchemaClientUsesTheScopedVersionsCollectionForListingAndPublication()
    {
        var scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var resource = Schema(scope, new ResourceNamespace("shared.contracts"));
        var version = new SchemaVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SchemaResourceKinds.SchemaVersion,
            Metadata = new() { Name = "contact-1-0-0", Namespace = resource.Namespace },
            ScopeRef = scope,
            SchemaUid = resource.Uid,
            SchemaName = resource.Name,
            SchemaNamespace = resource.Namespace,
            SchemaGeneration = resource.Generation,
            Version = "1.0.0",
            Format = resource.Definition.Format,
            Content = resource.Definition.Content,
            DefinitionHash = "sha256:test",
            PublishedAt = DateTimeOffset.UtcNow
        };
        var requests = new List<(HttpMethod Method, string Path)>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            var isVersionDetails = request.RequestUri.AbsolutePath.EndsWith("/1.0.0", StringComparison.Ordinal);
            return new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.Created)
            {
                Content = request.Method == HttpMethod.Get && !isVersionDetails
                    ? JsonContent.Create(new Agentstration.Api.Contracts.ValueResponse<SchemaVersionResource>([version]))
                    : JsonContent.Create(version)
            };
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new SchemasApiClient(http);

        _ = await client.GetVersionsAsync(scope, resource.Namespace, resource.Name);
        _ = await client.GetVersionAsync(scope, resource.Namespace, resource.Name, "1.0.0");
        _ = await client.PublishAsync(scope, resource.Namespace, resource.Name, new("1.0.0"));

        const string expected = "/api/schemas/contact/versions?scopeRef=%2Fworkspaces%2Fbbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb&resourceNamespace=shared.contracts";
        Assert.AreEqual((HttpMethod.Get, expected), requests[0]);
        Assert.AreEqual((HttpMethod.Get, expected.Replace("/versions?", "/versions/1.0.0?", StringComparison.Ordinal)), requests[1]);
        Assert.AreEqual((HttpMethod.Post, expected), requests[2]);
    }

    private static SchemaResource Schema(ResourceScopeRef scope, ResourceNamespace @namespace) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = SchemaResourceKinds.Schema,
        Metadata = new() { Name = "contact", Namespace = @namespace },
        ScopeRef = scope,
        Generation = 2,
        Definition = new()
        {
            DisplayName = "Contact",
            Format = SchemaFormatIds.JsonSchema,
            Content = JsonSerializer.SerializeToElement(new { type = "object" })
        }
    };
}
