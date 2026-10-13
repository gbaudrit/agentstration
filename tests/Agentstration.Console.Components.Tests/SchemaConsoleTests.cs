using System.Text.Json;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.Schemas;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SchemaConsoleTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [TestMethod]
    public void LibraryShowsScopeNamespaceAndFormatForDrafts()
    {
        using var context = Context(new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]));

        var rendered = context.Render<Agentstration.Web.Components.Pages.Schemas>();

        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='schema-row']"));
            StringAssert.Contains(rendered.Markup, "shared.contracts/contact");
            StringAssert.Contains(rendered.Markup, SchemaFormatIds.JsonSchema);
            StringAssert.Contains(rendered.Markup, Scope.Value);
            StringAssert.Contains(rendered.Find("[data-testid='schema-details-link']").GetAttribute("href"), "/schemas/contact");
        });
    }

    [TestMethod]
    public void CreationDerivesTechnicalNameFromDisplayName()
    {
        using var context = Context(new SchemaClientStub([]));

        var rendered = context.Render<SchemaEditor>();

        rendered.Find("[data-testid='schema-display-name']").Input("Customer address");

        Assert.AreEqual("customer-address", rendered.Find("[data-testid='schema-name']").GetAttribute("value"));
    }

    [TestMethod]
    public void DetailsExposeOverviewDefinitionAndYamlTabs()
    {
        using var context = Context(new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/schemas/contact?namespace=shared.contracts&scopeRef={Uri.EscapeDataString(Scope.Value)}");

        var rendered = context.Render<SchemaEditor>(parameters => parameters.Add(value => value.Name, "contact"));
        var strings = context.Services.GetRequiredService<IStringLocalizer<SchemasStrings>>();

        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='schema-overview']")));
        Assert.HasCount(1, rendered.FindAll("[data-testid='schema-editor'] .resource-icon svg.ui-icon"));
        CollectionAssert.AreEqual(
            new[] { strings["Tab.Overview"].Value, strings["Tab.Definition"].Value, strings["Tab.Versions"].Value, strings["Tab.Yaml"].Value },
            rendered.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()).ToArray());

        rendered.Find("[data-testid='schema-tab-definition']").Click();
        Assert.HasCount(1, rendered.FindAll("[data-testid='schema-source']"));
        Assert.HasCount(1, rendered.FindAll("[data-testid='schema-visual-editor']"));
        Assert.HasCount(1, rendered.FindAll("[data-testid='schema-preview-mode']"));

        rendered.Find("[data-testid='schema-definition-source-mode']").Click();
        Assert.HasCount(1, rendered.FindAll("[data-testid='schema-source-editor']"));
        rendered.Find("[data-testid='schema-preview-mode']").Click();
        Assert.HasCount(1, rendered.FindAll("[data-testid='schema-source-editor']"));
        Assert.IsTrue(rendered.Find(".schema-definition-source").HasAttribute("hidden"));

        rendered.Find("[data-testid='schema-tab-yaml']").Click();
        var yamlEditor = rendered.Find("[data-testid='schema-yaml-source']");
        var yaml = yamlEditor.GetAttribute("value") ?? yamlEditor.TextContent;
        StringAssert.Contains(yaml, "kind: Schema");
        StringAssert.Contains(yaml, "name: contact");
    }

    [TestMethod]
    public void VersionsTabPublishesTheRequestedImmutableVersion()
    {
        var client = new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]);
        using var context = Context(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/schemas/contact?namespace=shared.contracts&scopeRef={Uri.EscapeDataString(Scope.Value)}");
        var rendered = context.Render<SchemaEditor>(parameters => parameters.Add(value => value.Name, "contact"));

        rendered.WaitForElement("[data-testid='schema-tab-versions']").Click();
        rendered.Find("[data-testid='schema-publish-version']").Change("2.1.0");
        rendered.Find("[data-testid='schema-publish']").Click();

        rendered.WaitForAssertion(() => Assert.AreEqual("2.1.0", client.PublishedVersion));
        var versionLink = rendered.WaitForElement("[data-testid='schema-version-link']");
        var href = versionLink.GetAttribute("href");
        StringAssert.Contains(href, "view=versions&version=2.1.0");
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(href!);
        rendered.Render(parameters => parameters.Add(value => value.Name, "contact"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("2.1.0", client.RequestedVersion);
            StringAssert.Contains(rendered.Find("[data-testid='schema-version-content']").TextContent, "object");
        });
    }

    [TestMethod]
    public void YamlTabUpdatesTheDraftThroughTheSameConcurrencyAwareClient()
    {
        var client = new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]);
        using var context = Context(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/schemas/contact?namespace=shared.contracts&scopeRef={Uri.EscapeDataString(Scope.Value)}");
        var rendered = context.Render<SchemaEditor>(parameters => parameters.Add(value => value.Name, "contact"));

        rendered.WaitForElement("[data-testid='schema-tab-yaml']").Click();
        var editor = rendered.Find("[data-testid='schema-yaml-source']");
        var yaml = editor.GetAttribute("value") ?? editor.TextContent;
        editor.Input(yaml.Replace("displayName: Contact", "displayName: Updated contact", StringComparison.Ordinal));
        rendered.Find("[data-testid='schema-yaml-save']").Click();

        rendered.WaitForAssertion(() => Assert.AreEqual("Updated contact", client.UpdatedProperties?.DisplayName));
    }

    [TestMethod]
    public void UnknownFormatRemainsInspectableWithExplicitDiagnostic()
    {
        using var context = Context(new SchemaClientStub([Schema("application/protobuf")]));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/schemas/contact?namespace=shared.contracts&scopeRef={Uri.EscapeDataString(Scope.Value)}");

        var rendered = context.Render<SchemaEditor>(parameters => parameters.Add(value => value.Name, "contact"));

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "application/protobuf");
            Assert.HasCount(1, rendered.FindAll(".form-alert-warning"));
        });
        rendered.Find("[data-testid='schema-tab-definition']").Click();
        Assert.IsTrue(rendered.FindAll("input[disabled]")
            .Any(input => input.GetAttribute("value") == "application/protobuf"));
        StringAssert.Contains(rendered.FindComponent<SchemaDocumentEditor>().Instance.Value.GetRawText(), "object");
    }

    [TestMethod]
    public void DefinitionEditorAddsAPropertyAndPreservesItForDraftSave()
    {
        var client = new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]);
        using var context = Context(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/schemas/contact?namespace=shared.contracts&scopeRef={Uri.EscapeDataString(Scope.Value)}");
        var rendered = context.Render<SchemaEditor>(parameters => parameters.Add(value => value.Name, "contact"));

        rendered.WaitForElement("[data-testid='schema-tab-definition']").Click();
        rendered.Find("[data-testid='schema-add-property']").Click();
        rendered.Find("[data-testid='schema-save']").Click();

        rendered.WaitForAssertion(() => Assert.IsTrue(client.UpdatedProperties?.Content
            .GetProperty("properties").TryGetProperty("field1", out _)));
    }

    [TestMethod]
    public void ComplexJsonSchemaUsesSourceOnlyWithoutChangingTheDocument()
    {
        const string advanced = "{\"type\":\"object\",\"oneOf\":[{\"required\":[\"email\"]},{\"required\":[\"phone\"]}]}";
        using var context = Context(new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema, advanced)]));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/schemas/contact?namespace=shared.contracts&scopeRef={Uri.EscapeDataString(Scope.Value)}");
        var rendered = context.Render<SchemaEditor>(parameters => parameters.Add(value => value.Name, "contact"));

        rendered.WaitForElement("[data-testid='schema-tab-definition']").Click();

        Assert.HasCount(1, rendered.FindAll("[data-testid='schema-source-only']"));
        Assert.HasCount(0, rendered.FindAll("[data-testid='schema-visual-mode']"));
        Assert.IsTrue(JsonElement.DeepEquals(JsonDocument.Parse(advanced).RootElement,
            rendered.FindComponent<SchemaDocumentEditor>().Instance.Value));
    }

    [TestMethod]
    public async Task MalformedSourceShowsAnErrorWithoutReplacingTheCurrentDefinition()
    {
        using var context = Context(new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/schemas/contact?namespace=shared.contracts&scopeRef={Uri.EscapeDataString(Scope.Value)}");
        var rendered = context.Render<SchemaEditor>(parameters => parameters.Add(value => value.Name, "contact"));

        rendered.WaitForElement("[data-testid='schema-tab-definition']").Click();
        rendered.Find("[data-testid='schema-definition-source-mode']").Click();
        var sourceEditor = rendered.FindComponent<SchemaSourceEditor>();
        await rendered.InvokeAsync(() => sourceEditor.Instance.ValueChanged.InvokeAsync("not-json"));
        rendered.Find("[data-testid='schema-apply-source']").Click();

        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[role='alert']")));
        Assert.AreEqual("object", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value
            .GetProperty("type").GetString());
    }

    private static BunitContext Context(ISchemasClient client)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(client);
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ScopeInventoryClient());
        return context;
    }

    private static SchemaResource Schema(string format, string content = "{\"type\":\"object\"}") => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = SchemaResourceKinds.Schema,
        Metadata = new() { Name = "contact", Namespace = new("shared.contracts") },
        ScopeRef = Scope,
        Generation = 2,
        Definition = new()
        {
            DisplayName = "Contact",
            Format = format,
            Dialect = format == SchemaFormatIds.JsonSchema ? "https://json-schema.org/draft/2020-12/schema" : null,
            Content = JsonDocument.Parse(content).RootElement.Clone()
        }
    };

    private sealed class SchemaClientStub(IReadOnlyList<SchemaResource> schemas) : ISchemasClient
    {
        public string? PublishedVersion { get; private set; }
        public string? RequestedVersion { get; private set; }
        public SchemaProperties? UpdatedProperties { get; private set; }
        private readonly List<SchemaVersionResource> versions = [];

        public Task<IReadOnlyList<SchemaResource>> GetSchemasAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(schemas);
        public Task<IReadOnlyList<SchemaFormatDescriptor>> GetFormatsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchemaFormatDescriptor>>([
                new(SchemaFormatIds.JsonSchema, "JSON Schema",
                    new(BuiltinJsonSchemaFormatHandler.ProviderId, BuiltinJsonSchemaFormatHandler.ProviderVersion),
                    [BuiltinJsonSchemaFormatHandler.Draft202012],
                    SchemaFormatCapabilities.ValidateDefinition | SchemaFormatCapabilities.ValidateInstance
                        | SchemaFormatCapabilities.VisualAuthoring | SchemaFormatCapabilities.InstancePreview,
                    true)
            ]);
        public Task<SchemaFormatValidationResult> ValidateDefinitionAsync(ValidateSchemaDefinitionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(SchemaFormatValidationResult.Valid);
        public Task<SchemaFormatValidationResult> ValidateInstanceAsync(ValidateSchemaInstanceRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(SchemaFormatValidationResult.Valid);
        public Task<ResourceSnapshot<SchemaResource>> GetSchemaAsync(ResourceScopeRef scopeRef,
            ResourceNamespace ns, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceSnapshot<SchemaResource>(schemas.Single(), "\"etag\""));
        public Task<ResourceSnapshot<SchemaResource>> CreateAsync(CreateSchemaRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SchemaResource>> UpdateAsync(ResourceScopeRef scopeRef,
            ResourceNamespace ns, string name, PutSchemaRequest request, string etag,
            CancellationToken cancellationToken = default)
        {
            UpdatedProperties = request.Properties;
            return Task.FromResult(
                new ResourceSnapshot<SchemaResource>(schemas.Single() with { Definition = request.Properties }, "\"etag-2\""));
        }

        public Task<IReadOnlyList<SchemaVersionResource>> GetVersionsAsync(ResourceScopeRef scopeRef,
            ResourceNamespace ns, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchemaVersionResource>>(versions);

        public Task<SchemaVersionResource> GetVersionAsync(ResourceScopeRef scopeRef, ResourceNamespace ns,
            string name, string version, CancellationToken cancellationToken = default)
        {
            RequestedVersion = version;
            return Task.FromResult(versions.Single(value => value.Version == version));
        }

        public Task<SchemaVersionResource> PublishAsync(ResourceScopeRef scopeRef, ResourceNamespace ns,
            string name, PublishSchemaVersionRequest request, CancellationToken cancellationToken = default)
        {
            PublishedVersion = request.Version;
            var current = schemas.Single();
            var version = new SchemaVersionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SchemaResourceKinds.SchemaVersion,
                Metadata = new() { Name = $"{name}-{request.Version}", Namespace = ns },
                ScopeRef = scopeRef,
                SchemaUid = current.Uid,
                SchemaName = name,
                SchemaNamespace = ns,
                SchemaGeneration = current.Generation,
                Version = request.Version,
                Format = current.Definition.Format,
                Dialect = current.Definition.Dialect,
                Content = current.Definition.Content,
                DefinitionHash = "sha256:test",
                PublishedAt = DateTimeOffset.UtcNow
            };
            versions.Add(version);
            return Task.FromResult(version);
        }
    }

    private sealed class ScopeInventoryClient : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([
                new(Scope, ResourceScopeKind.Workspace, "Default", true)
            ]);
    }
}
