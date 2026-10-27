using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
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
    public async Task FlowPublicationPinsReusableSchemaAndRejectsUnsafeBindings()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>()
            .Push(requestContext);
        var workspaceScope = ResourceScopeRef.Workspace(requestContext.WorkspaceId);
        var workspaceId = new WorkspaceId(requestContext.WorkspaceId);
        var schemaService = factory.Services.GetRequiredService<SchemaManagementService>();
        var bindings = factory.Services.GetRequiredService<ISchemaBindingResolver>();
        var flows = factory.Services.GetRequiredService<FlowService>();
        var original = Definition(
            "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}");
        var createdSchema = await schemaService.CreateAsync(
            Resource(workspaceScope, "flow-input", original), default);
        var schemaVersion = await schemaService.PublishAsync(workspaceScope, ResourceNamespace.Default,
            "flow-input", "1.0.0", default);
        var reference = new SchemaBinding
        {
            Reference = new()
            {
                Name = "flow-input",
                Version = "1.0.0",
                ScopeRef = workspaceScope
            }
        };
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchemaBinding = reference,
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new TransformFlowStepDefinition
                {
                    Name = "normalize",
                    Mapping = JsonSerializer.SerializeToElement("${transition.output}"),
                    SchemaBinding = reference
                },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success }
            ],
            Transitions =
            [
                new("normalize", "input", "completed", "normalize"),
                new("completed", "normalize", "completed", "completed")
            ]
        };
        var createdFlow = await flows.CreateAsync(workspaceId, new CreateFlowCommand(
            "schema-bound-flow", null, "1.0.0", true,
            new WorkflowFlowDefinition(
                "input",
                [new FlowNode("input", FlowNodeKind.Input)],
                []),
            Graph: graph), default);

        var published = await flows.PublishVersionAsync(workspaceId, createdFlow.Value.Id,
            "1.0.0", false, default);

        Assert.IsNull(published.Value.Graph!.InputSchemaBinding);
        Assert.AreEqual("string", published.Value.Graph.InputSchema!.Value.GetProperty("properties")
            .GetProperty("name").GetProperty("type").GetString());
        var publishedTransform = published.Value.Graph.Steps.OfType<TransformFlowStepDefinition>().Single();
        Assert.IsNull(publishedTransform.SchemaBinding);
        Assert.AreEqual("string", publishedTransform.Schema!.Value.GetProperty("properties")
            .GetProperty("name").GetProperty("type").GetString());
        Assert.AreEqual("string", published.Value.Graph.GetOutputs().Single().Schema!.Value
            .GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
        var snapshot = published.Value.SchemaBindings!["$.inputSchema"];
        Assert.AreEqual(schemaVersion.Value.DefinitionHash, snapshot.DefinitionHash);
        Assert.AreEqual(schemaVersion.Value.SchemaUid, snapshot.SchemaUid);
        Assert.AreEqual("1.0.0", snapshot.Source!.Version);
        Assert.AreEqual(schemaVersion.Value.DefinitionHash,
            published.Value.SchemaBindings["$.steps[normalize].schema"].DefinitionHash);

        using var client = factory.CreateClient();
        var publishedResponse = await client.GetFromJsonAsync<FlowVersionResponse>(
            "/api/flows/schema-bound-flow/versions/1.0.0");
        Assert.IsNotNull(publishedResponse);
        Assert.AreEqual("1.0.0", publishedResponse.SchemaBindings!["$.inputSchema"].Source!.Version);

        _ = await schemaService.PutAsync(workspaceScope, ResourceNamespace.Default, "flow-input",
            original with { Content = JsonDocument.Parse("{\"type\":\"array\"}").RootElement.Clone() },
            createdSchema.ETag, default);
        var reloaded = await flows.GetVersionAsync(workspaceId, createdFlow.Value.Id, "1.0.0", default);
        Assert.AreEqual("object", reloaded!.Value.Graph!.InputSchema!.Value.GetProperty("type").GetString());
        Assert.AreEqual(schemaVersion.Value.DefinitionHash,
            reloaded.Value.SchemaBindings!["$.inputSchema"].DefinitionHash);

        var forbidden = await Assert.ThrowsAsync<SchemaBindingResolutionException>(async () =>
            await bindings.ResolveAsync("$.inputSchema", reference,
                new(ResourceScopeRef.Workspace(Guid.NewGuid()), ResourceNamespace.Default,
                    SchemaFormatIds.JsonSchema), default));
        Assert.AreEqual("schema_reference_forbidden", forbidden.Code);

        var missing = await Assert.ThrowsAsync<SchemaBindingResolutionException>(async () =>
            await bindings.ResolveAsync("$.inputSchema", new SchemaBinding
            {
                Reference = new()
                {
                    Name = "flow-input",
                    Version = "9.9.9",
                    ScopeRef = workspaceScope
                }
            }, new(workspaceScope, ResourceNamespace.Default, SchemaFormatIds.JsonSchema), default));
        Assert.AreEqual("schema_version_not_found", missing.Code);

        var incompatible = await Assert.ThrowsAsync<SchemaBindingResolutionException>(async () =>
            await bindings.ResolveAsync("$.inputSchema", new SchemaBinding
            {
                Inline = new()
                {
                    Format = "xml-schema",
                    Content = JsonDocument.Parse("{}").RootElement.Clone()
                }
            }, new(workspaceScope, ResourceNamespace.Default, SchemaFormatIds.JsonSchema), default));
        Assert.AreEqual("schema_format_incompatible", incompatible.Code);

        var unavailable = await Assert.ThrowsAsync<SchemaBindingResolutionException>(async () =>
            await bindings.ResolveAsync("$.inputSchema", new SchemaBinding
            {
                Inline = new()
                {
                    Format = "unavailable-format",
                    Content = JsonDocument.Parse("{}").RootElement.Clone()
                }
            }, new(workspaceScope, ResourceNamespace.Default, "unavailable-format"), default));
        Assert.AreEqual("schema_format_unavailable", unavailable.Code);
    }

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
