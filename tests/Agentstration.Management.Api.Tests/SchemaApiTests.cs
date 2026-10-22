using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SchemaApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SchemaDraftRoundTripsThroughApiAndPublishedVersionsAreImmutable()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        using var client = factory.CreateClient();
        var definition = Definition("{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}");

        using var create = await client.PostAsJsonAsync("/api/schemas", new CreateSchemaRequest(
            "contact", definition, new ResourceNamespace("crm"), scopeRef));
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<SchemaResource>();
        Assert.IsNotNull(created);
        Assert.AreEqual("contact", created.Name);
        Assert.AreEqual("crm", created.Namespace.Value);
        Assert.AreEqual(SchemaFormatIds.JsonSchema, created.Definition.Format);
        Assert.IsTrue(create.Headers.ETag is not null);

        var service = factory.Services.GetRequiredService<SchemaManagementService>();
        var first = await service.PublishAsync(scopeRef, created.Namespace, created.Name, "1.0.0", default);
        var updated = await service.PutAsync(scopeRef, created.Namespace, created.Name,
            definition with
            {
                Content = JsonDocument.Parse("{\"properties\":{\"name\":{\"type\":\"string\"}},\"type\":\"object\"}").RootElement.Clone()
            }, create.Headers.ETag.Tag, default);
        var idempotent = await service.PublishAsync(scopeRef, created.Namespace, created.Name, "1.0.0", default);
        Assert.AreEqual(first.Value.DefinitionHash, idempotent.Value.DefinitionHash);

        _ = await service.PutAsync(scopeRef, created.Namespace, created.Name,
            updated.Value.Definition with
            {
                Content = JsonDocument.Parse("{\"type\":\"array\"}").RootElement.Clone()
            }, updated.ETag, default);
        var immutable = await Assert.ThrowsAsync<SchemaValidationException>(() =>
            service.PublishAsync(scopeRef, created.Namespace, created.Name, "1.0.0", default));
        Assert.AreEqual("schema_version_immutable", immutable.Code);
        var persisted = await service.GetVersionAsync(scopeRef, created.Namespace, created.Name, "1.0.0", default);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(JsonValueKind.Object, persisted.Value.Content.ValueKind);
        Assert.AreEqual("object", persisted.Value.Content.GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task SchemaFormatAndDialectCannotChangeForAnExistingIdentity()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var service = factory.Services.GetRequiredService<SchemaManagementService>();
        var created = await service.CreateAsync(Resource(scopeRef, "immutable-format", Definition("{\"type\":\"string\"}")), default);

        var dialect = await Assert.ThrowsAsync<SchemaValidationException>(() => service.PutAsync(scopeRef,
            ResourceNamespace.Default, created.Value.Name,
            created.Value.Definition with { Dialect = "https://json-schema.org/draft/2019-09/schema" },
            created.ETag, default));
        Assert.AreEqual("schema_format_immutable", dialect.Code);

        var unsupported = await Assert.ThrowsAsync<SchemaValidationException>(() => service.CreateAsync(
            Resource(scopeRef, "protobuf", Definition("{}") with { Format = "protobuf" }), default));
        Assert.AreEqual("schema_format_unavailable", unsupported.Code);
    }

    [TestMethod]
    public async Task BuiltinJsonSchemaValidationReturnsPathSpecificDefinitionAndInstanceIssues()
    {
        var handler = new BuiltinJsonSchemaFormatHandler();
        var invalidDefinition = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"age\":{\"type\":\"integer\",\"oneOf\":[]}}}").RootElement;

        var definitionResult = await handler.ValidateDefinitionAsync(invalidDefinition,
            BuiltinJsonSchemaFormatHandler.Draft202012, default);

        Assert.IsFalse(definitionResult.IsValid);
        Assert.IsTrue(definitionResult.Issues.Any(issue => issue.Code == "json_schema_keyword_unsupported"
            && issue.Path == "$.properties.age.oneOf"));

        var definition = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"age\":{\"type\":\"integer\",\"minimum\":18}},\"required\":[\"age\"],\"additionalProperties\":false}").RootElement;
        var instance = JsonDocument.Parse("{\"age\":16,\"hidden\":true}").RootElement;
        var instanceResult = await handler.ValidateInstanceAsync(definition, instance,
            BuiltinJsonSchemaFormatHandler.Draft202012, default);

        Assert.IsFalse(instanceResult.IsValid);
        Assert.IsTrue(instanceResult.Issues.Any(issue => issue.Code == "json_schema_instance_minimum"
            && issue.Path == "$.age"));
        Assert.IsTrue(instanceResult.Issues.Any(issue => issue.Code == "json_schema_instance_additional_property"
            && issue.Path == "$.hidden"));
    }

    [TestMethod]
    public async Task FormatRegistryAndValidationAreAvailableThroughTheManagementApi()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        using var client = factory.CreateClient();

        var formats = await client.GetFromJsonAsync<Agentstration.Api.Contracts.ValueResponse<SchemaFormatDescriptor>>(
            "/api/schemas/formats");
        Assert.IsNotNull(formats);
        var jsonSchema = formats.Value.Single(value => value.Format == SchemaFormatIds.JsonSchema);
        Assert.AreEqual(BuiltinJsonSchemaFormatHandler.ProviderId, jsonSchema.Provider.Id);
        Assert.IsTrue(jsonSchema.Capabilities.HasFlag(SchemaFormatCapabilities.ValidateInstance));

        var definition = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}").RootElement.Clone();
        using var response = await client.PostAsJsonAsync("/api/schemas/formats/validate-instance",
            new ValidateSchemaInstanceRequest(SchemaFormatIds.JsonSchema,
                BuiltinJsonSchemaFormatHandler.Draft202012, definition,
                JsonSerializer.SerializeToElement(new { })));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var validation = await response.Content.ReadFromJsonAsync<SchemaFormatValidationResult>();
        Assert.IsNotNull(validation);
        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Issues.Any(issue => issue.Path == "$.name"
            && issue.Code == "json_schema_instance_required"));
    }

    [TestMethod]
    public void SchemaNativeContentRoundTripsThroughJsonAndYamlManifests()
    {
        var resource = Resource(ResourceScopeRef.Instance, "manifest", Definition(
            "{\"type\":\"object\",\"properties\":{\"enabled\":{\"type\":\"boolean\"}}}"));

        var json = ResourceManifestSerializer.ToJson(resource);
        var fromJson = ResourceManifestSerializer.FromJsonStrict<SchemaResource>(json);
        var yaml = ResourceManifestSerializer.ToYaml(resource);
        var fromYaml = ResourceManifestSerializer.FromYaml<SchemaResource>(yaml);

        Assert.AreEqual("boolean", fromJson.Definition.Content.GetProperty("properties")
            .GetProperty("enabled").GetProperty("type").GetString());
        Assert.AreEqual(fromJson.Definition.Content.GetRawText(), fromYaml.Definition.Content.GetRawText());
        Assert.AreEqual(resource.Definition.Format, fromYaml.Definition.Format);
        Assert.AreEqual(resource.Definition.Dialect, fromYaml.Definition.Dialect);
    }

    private static SchemaResource Resource(ResourceScopeRef scopeRef, string name, SchemaProperties definition) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = SchemaResourceKinds.Schema,
        Metadata = new() { Name = name },
        ScopeRef = scopeRef,
        Definition = definition
    };

    private static SchemaProperties Definition(string json) => new()
    {
        DisplayName = "Contact",
        Description = "Reusable contact payload.",
        Format = SchemaFormatIds.JsonSchema,
        Dialect = "https://json-schema.org/draft/2020-12/schema",
        Content = JsonDocument.Parse(json).RootElement.Clone()
    };
}
