using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Contracts;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Components.Values;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolRelayEditorTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [TestMethod]
    [DataRow("en-US", "Hide")]
    [DataRow("fr-FR", "Masquer")]
    public void OptionalObjectCanBeHiddenSavedAndReopenedWithoutItsRequiredChildren(string culture, string label)
    {
        using var currentCulture = new TestCultureScope(culture);
        using var handler = new ApiHandler("""{"type":"object","properties":{"acquisition":{"type":"object","properties":{"id":{"type":"string"},"uid":{"type":"string"}},"required":["id","uid"]},"query":{"type":"string"}},"required":["query"]}""")
        { InitialBindings = [] };
        using var context = Context(handler);
        var page = context.Render<ToolRelayEditor>(p => p.Add(c => c.Name, "relay"));
        page.WaitForElement("[data-requirement-id='/acquisition'] [data-testid='relay-binding-kind']");
        Assert.AreEqual(label, page.Find("[data-requirement-id='/acquisition'] option[value='Omitted']").TextContent);
        Assert.HasCount(0, page.FindAll("[data-requirement-id='/acquisition/id'] option[value='Omitted']"));
        Assert.HasCount(0, page.FindAll("[data-requirement-id='/query'] option[value='Omitted']"));
        page.Find("[data-requirement-id='/acquisition'] [data-testid='relay-binding-kind']").Change("Omitted");
        page.WaitForAssertion(() => Assert.HasCount(0, page.FindAll("[data-requirement-id='/acquisition/id']")));
        Assert.HasCount(0, page.FindAll("[data-requirement-id='/acquisition/uid']"));
        page.Find("[data-testid='relay-save-draft']").Click();
        page.WaitForAssertion(() => Assert.IsNotNull(handler.Saved));
        var binding = ((RelayToolDefinitionProperties)handler.Saved!.Properties).Bindings.Single();
        Assert.IsInstanceOfType<OmittedToolArgumentBinding>(binding);
        Assert.AreEqual("/acquisition", binding.Path);
        page.Dispose();
        var reopened = context.Render<ToolRelayEditor>(p => p.Add(c => c.Name, "relay"));
        reopened.WaitForAssertion(() => Assert.AreEqual(ValueBindingSourceKind.Omitted,
            reopened.FindComponents<ValueBindingRequirementEditor>().Single(c => c.Instance.Requirement.Id == "/acquisition").Instance.Selection.Kind));
        Assert.HasCount(0, reopened.FindAll("[data-requirement-id='/acquisition/id']"));
        Assert.IsFalse(reopened.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
    }

    [TestMethod]
    [DataRow("en-US", "ToolDefinition version", "Target source Tool version / revision")]
    [DataRow("fr-FR", "Version de la ToolDefinition", "Version / révision du Tool source ciblée")]
    public void EditorSeparatesRelayAndSourceVersionsAndShowsValueSources(string locale, string relayLabel, string sourceLabel)
    {
        using var culture = new TestCultureScope(locale);
        using var handler = new ApiHandler();
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, relayLabel);
            StringAssert.Contains(rendered.Markup, sourceLabel);
            Assert.AreEqual("1.2.0", rendered.Find("[data-testid='relay-version']").GetAttribute("value"));
            StringAssert.Contains(rendered.Find("[data-testid='relay-source']").TextContent, "2.4.0");
            Assert.IsTrue(rendered.FindAll("[data-testid='relay-binding-kind'] option").Any(option => option.GetAttribute("value") == "Parameter"));
            Assert.IsTrue(rendered.FindAll("[data-testid='relay-binding-kind'] option").Any(option => option.GetAttribute("value") == "Secret"));
            Assert.IsFalse(rendered.Find("[data-testid='relay-public-schema']").TextContent.Contains("\"id\"", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    [DataRow("{\"type\":\"string\"}", "\"\"")]
    [DataRow("{\"type\":[\"string\",\"null\"]}", "null")]
    [DataRow("{\"type\":\"boolean\"}", "false")]
    [DataRow("{\"type\":\"number\"}", "1.5")]
    [DataRow("{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}", "[1,2]")]
    [DataRow("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"},\"items\":{\"type\":\"array\",\"items\":{\"type\":\"boolean\"}}}}", "{\"value\":\"keep this property\",\"items\":[true,false]}")]
    public void SharedLiteralSavesAndReopensTheActualJsonValue(string propertySchema, string json)
    {
        using var handler = new ApiHandler("{\"type\":\"object\",\"properties\":{\"query\":" + propertySchema + "}}") { InitialBindings = [] };
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        rendered.WaitForElement("[data-requirement-id='/query'] [data-testid='relay-binding-kind']").Change("Literal");
        Assert.IsTrue(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
        Assert.IsTrue(rendered.FindComponent<ValueBindingRequirementEditor>().Instance.Requirement.Required);
        EnterLiteral(rendered, "/query", json);
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled")));
        rendered.Find("[data-testid='relay-save-draft']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Saved));
        var saved = (RelayToolDefinitionProperties)handler.Saved!.Properties;
        var literal = (LiteralToolArgumentBinding)saved.Bindings.Single();
        Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), literal.Value));
        Assert.AreEqual(handler.Source.Uid, saved.Target.Uid);
        Assert.AreEqual(handler.Source.Generation, saved.Target.Generation);
        Assert.IsFalse(saved.Publish);
        Assert.AreEqual(0, handler.Publications);

        using var reopenedContext = Context(handler);
        var reopened = reopenedContext.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        reopened.WaitForAssertion(() =>
        {
            var editor = reopened.FindComponents<ValueBindingRequirementEditor>().Single(value => value.Instance.Requirement.Id == "/query");
            Assert.IsTrue(JsonElement.DeepEquals(literal.Value, editor.Instance.Selection.Literal!.Value));
            Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(propertySchema), editor.Instance.Requirement.LiteralSchema!.Value));
            Assert.IsFalse(reopened.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public void MalformedRawLiteralBlocksSavingAndPublicationAcrossParentRenders()
    {
        using var handler = new ApiHandler();
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        rendered.WaitForElement("[data-testid='relay-publish-review']").Click();
        EnterLiteral(rendered, "/store", "{broken");
        Assert.IsTrue(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
        Assert.IsTrue(rendered.Find("[data-testid='relay-publish-review']").HasAttribute("disabled"));
        Assert.HasCount(0, rendered.FindAll("[data-testid='relay-publication-review']"));
        rendered.Find("[data-testid='relay-version']").Change("1.3.0");
        Assert.IsTrue(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
        Assert.AreEqual("{broken", rendered.Find("[data-binding-path='/store'] textarea").GetAttribute("value"));
        Assert.IsNull(handler.Saved);
        Assert.AreEqual(0, handler.Publications);
        rendered.Find("[data-binding-path='/store'] textarea").Input("{\"id\":\"updated\"}");
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled")));
        rendered.Find("[data-testid='relay-save-draft']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Saved));
        Assert.AreEqual("updated", ((LiteralToolArgumentBinding)((RelayToolDefinitionProperties)handler.Saved!.Properties).Bindings.Single()).Value.GetProperty("id").GetString());
    }

    [TestMethod]
    [DataRow("", false)]
    [DataRow("not json", true)]
    [DataRow("null", false)]
    public async Task MissingOrInvalidLiteralCannotCreateADraft(string raw, bool afterValid)
    {
        using var handler = new ApiHandler("""{"type":"object","properties":{"query":{"type":"string","default":"do not silently use this"}}}""") { InitialBindings = [] };
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters
            .Add(value => value.CreationIdentity, new() { Name = "relay", DisplayName = "Relay", ScopeRef = Scope })
            .Add(value => value.Source, handler.Source.Name));
        rendered.WaitForElement("[data-requirement-id='/query'] [data-testid='relay-binding-kind']").Change("Literal");
        if (afterValid) EnterLiteral(rendered, "/query", "\"valid\"");
        EnterLiteral(rendered, "/query", raw);
        Assert.IsTrue(rendered.Find("[data-testid='tool-definition-create-draft']").HasAttribute("disabled"));
        await rendered.InvokeAsync(rendered.Instance.CreateDraftAsync);
        Assert.HasCount(0, handler.Writes);
    }

    [TestMethod]
    public void ChangingSourceClearsMalformedLiteralAndDoesNotRestoreItsOldPayload()
    {
        using var handler = new ApiHandler();
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        EnterLiteral(rendered, "/store", "{broken");
        rendered.Find("[data-requirement-id='/store'] [data-testid='relay-binding-kind']").Change("Caller");
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled")));
        rendered.Find("[data-requirement-id='/store'] [data-testid='relay-binding-kind']").Change("Literal");
        var editor = rendered.FindComponents<ValueBindingRequirementEditor>().Single(value => value.Instance.Requirement.Id == "/store");
        Assert.IsNull(editor.Instance.Selection.Literal);
        Assert.IsTrue(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
    }

    [TestMethod]
    public void ReadOnlyRelayDisablesSharedLiteralControls()
    {
        using var handler = new ApiHandler();
        using var context = Context(handler, ResourceScopeRef.Instance);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        rendered.WaitForAssertion(() =>
        {
            Assert.IsTrue(rendered.FindComponents<ValueBindingRequirementEditor>().All(value => value.Instance.Disabled));
            Assert.IsTrue(rendered.Find("[data-binding-path='/store'] [data-testid='schema-raw-mode']").HasAttribute("disabled"));
            Assert.IsTrue(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
        });
        Assert.HasCount(0, handler.Writes);
    }

    private static void EnterLiteral(IRenderedComponent<ToolRelayEditor> rendered, string path, string json)
    {
        rendered.FindAll($"[data-binding-path='{path}'] [data-testid='schema-raw-mode']").FirstOrDefault()?.Click();
        rendered.WaitForElement($"[data-binding-path='{path}'] textarea").Input(json);
    }

    [TestMethod]
    public void SharedRequirementRetainsUnionAndUnconstrainedTypeMetadata()
    {
        using var handler = new ApiHandler("""{"type":"object","properties":{"query":{"type":["string","null"]},"dynamic":{}}}""") { InitialBindings = [] };
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        rendered.WaitForAssertion(() =>
        {
            var editors = rendered.FindComponents<ValueBindingRequirementEditor>();
            Assert.AreEqual("[\"string\",\"null\"]", editors.Single(value => value.Instance.Requirement.Id == "/query").Instance.Requirement.ValueType);
            Assert.AreEqual("any", editors.Single(value => value.Instance.Requirement.Id == "/dynamic").Instance.Requirement.ValueType);
        });
    }

    [TestMethod]
    [DataRow("Parameter")]
    [DataRow("Secret")]
    public void SharedResourceSelectionSavesTheExactReferenceAndCallerRemovesIt(string kind)
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new ApiHandler();
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        var address = kind == "Parameter" ? handler.Parameter.Address : handler.Secret.Resource.Address;
        var scope = kind == "Parameter" ? handler.Parameter.ScopeRef!.Value : handler.Secret.Resource.ScopeRef!.Value;
        rendered.WaitForElement("[data-requirement-id='/query'] [data-testid='relay-binding-kind']").Change(kind);
        Assert.IsTrue(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
        rendered.Find("[data-testid='relay-binding-target']").Change($"{scope.Value}|{address.Namespace.Value}|{address.Name}");
        rendered.Find("[data-testid='relay-save-draft']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Saved));
        var saved = (RelayToolDefinitionProperties)handler.Saved!.Properties;
        var binding = saved.Bindings.Single(value => value.Path == "/query");
        if (binding is ParameterToolArgumentBinding parameter) Assert.AreEqual(new ParameterReference(address, scope), parameter.Parameter);
        else
        {
            var secret = (SecretToolArgumentBinding)binding;
            Assert.AreEqual(address, secret.Secret.Address);
            Assert.AreEqual(scope, secret.Secret.ScopeRef);
        }
        Assert.AreEqual("hr", ((LiteralToolArgumentBinding)saved.Bindings.Single(value => value.Path == "/store")).Value.GetProperty("id").GetString());
        Assert.AreEqual(handler.Source.Uid, saved.Target.Uid);
        Assert.AreEqual(handler.Source.Generation, saved.Target.Generation);
        Assert.IsFalse(saved.Publish);

        rendered.Find("[data-requirement-id='/query'] [data-testid='relay-binding-kind']").Change("Caller");
        rendered.Find("[data-testid='relay-save-draft']").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, ((RelayToolDefinitionProperties)handler.Saved!.Properties).Bindings));
        Assert.HasCount(0, rendered.FindAll("[data-testid='relay-binding-target']"));
        StringAssert.Contains(rendered.Find("[data-testid='relay-public-schema']").TextContent, "\"query\"");
        Assert.AreEqual(0, handler.Publications);
    }

    [TestMethod]
    public void UnavailableReferenceKeepsItsIdentityAndBlocksSavingUntilExplicitlyDiscarded()
    {
        using var culture = new TestCultureScope("en-US");
        var missing = new ParameterReference(ResourceAddress.Create(ResourceNamespace.Parse("other"), ParameterResourceKinds.Parameter, "missing"), ResourceScopeRef.Instance);
        using var handler = new ApiHandler { InitialBindings = [new ParameterToolArgumentBinding { Path = "/query", Parameter = missing }] };
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        var target = rendered.WaitForElement("[data-testid='relay-binding-target'] option[selected][disabled]");
        StringAssert.Contains(target.TextContent, "other/missing");
        var editor = rendered.FindComponents<ValueBindingRequirementEditor>().Single(value => value.Instance.Requirement.Id == "/query");
        Assert.AreEqual(new ScopedResourceAddress(missing.ScopeRef, missing.Address.Namespace, missing.Address.Kind, missing.Address.Name), editor.Instance.Selection.Resource);
        Assert.IsTrue(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
        rendered.Find("[data-requirement-id='/query'] [data-testid='relay-binding-kind']").Change("Caller");
        Assert.IsFalse(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
        Assert.IsNull(handler.Saved);
    }

    [TestMethod]
    public void SharedCreatorRefreshesOptionsAndSavesItsNewExactReference()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new ApiHandler();
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        rendered.WaitForElement("[data-requirement-id='/query'] [data-testid='relay-binding-kind']").Change("Parameter");
        rendered.Find("[data-testid='relay-binding-create']").Click();
        rendered.Find("[data-testid='contextual-parameter-display-name']").Change("Search query");
        rendered.Find("[data-testid='contextual-parameter-name']").Change("search-query");
        rendered.Find("[data-testid='contextual-parameter-value']").Change("hr");
        rendered.Find("[data-testid='contextual-parameter-create']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(handler.CreatedParameter);
            Assert.AreEqual(Scope, handler.CreatedParameter.ScopeRef);
            Assert.IsTrue(handler.ParameterReads >= 2);
            Assert.HasCount(0, rendered.FindAll("[role='dialog']"));
            Assert.IsFalse(rendered.Find("[data-testid='relay-save-draft']").HasAttribute("disabled"));
        });
        rendered.Find("[data-testid='relay-save-draft']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Saved));
        var binding = (ParameterToolArgumentBinding)((RelayToolDefinitionProperties)handler.Saved!.Properties).Bindings.Single(value => value.Path == "/query");
        Assert.AreEqual("search-query", binding.Parameter.Address.Name);
        Assert.AreEqual(ResourceNamespace.Default, binding.Parameter.Address.Namespace);
        Assert.AreEqual(Scope, binding.Parameter.ScopeRef);
        Assert.AreEqual(0, handler.Publications);
    }

    [TestMethod]
    [DataRow("/instance")]
    [DataRow("/tenants/22222222-2222-2222-2222-222222222222")]
    [DataRow("/workspaces/11111111-1111-1111-1111-111111111111")]
    public void SharedCreatorUsesTheRelayOwnerScope(string owner)
    {
        using var handler = new ApiHandler();
        using var context = Context(handler);
        var scope = ResourceScopeRef.Parse(owner);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters
            .Add(value => value.CreationIdentity, new() { Name = "relay", DisplayName = "Relay", ScopeRef = scope })
            .Add(value => value.Source, handler.Source.Name));
        rendered.WaitForAssertion(() =>
        {
            var editor = rendered.FindComponents<ValueBindingRequirementEditor>().Single(value => value.Instance.Requirement.Id == "/query");
            Assert.AreEqual(scope, editor.Instance.CreationContext?.ScopeRef);
            Assert.IsTrue(editor.Instance.Requirement.Required);
            Assert.AreEqual("string", editor.Instance.Requirement.ValueType);
        });
    }

    [TestMethod]
    public void IncompatibleSourceRetainsTheMissingBindingAndBlocksPublication()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new ApiHandler();
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        rendered.WaitForElement("[data-testid='relay-source']").Change($"{handler.Incompatible.Uid:D}:1");
        rendered.WaitForAssertion(() =>
        {
            Assert.IsTrue(rendered.Find("[data-testid='relay-publish-review']").HasAttribute("disabled"));
            StringAssert.Contains(rendered.Markup, "/store");
            StringAssert.Contains(rendered.Find("[role='alert']").TextContent, "Resolve missing");
        });
        Assert.AreEqual(0, handler.Publications);
    }

    [TestMethod]
    public void PublicationReviewRequiresExplicitConfirmationAndNeverWritesConsumers()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new ApiHandler();
        using var context = Context(handler);
        var rendered = context.Render<ToolRelayEditor>(parameters => parameters.Add(value => value.Name, "relay"));
        rendered.WaitForElement("[data-testid='relay-version']").Change("1.3.0");
        rendered.Find("[data-testid='relay-publish-review']").Click();
        Assert.AreEqual(0, handler.Publications);
        var review = rendered.Find("[data-testid='relay-publication-review']");
        StringAssert.Contains(review.TextContent, "1.3.0");
        StringAssert.Contains(review.TextContent, "2.4.0");
        StringAssert.Contains(review.TextContent, "Existing consumers keep their pinned versions");
        review.QuerySelector(".button-primary")!.Click();
        rendered.WaitForAssertion(() => Assert.AreEqual(1, handler.Publications));
        Assert.IsNotNull(handler.Saved);
        Assert.AreEqual("1.3.0", handler.Saved.Properties.Version);
        Assert.IsFalse(handler.Saved.Properties.Publish);
        Assert.AreEqual(handler.Source.Uid, ((RelayToolDefinitionProperties)handler.Saved.Properties).Target.Uid);
        Assert.IsFalse(handler.Writes.Any(path => path.Contains("agents", StringComparison.Ordinal) || path.Contains("toolsets", StringComparison.Ordinal) || path.Contains("flows", StringComparison.Ordinal)));
    }

    private static BunitContext Context(ApiHandler handler, ResourceScopeRef? navigationScope = null)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext(navigationScope ?? Scope));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        context.Services.AddSingleton<IToolDefinitionsClient>(new ToolDefinitionsApiClient(http));
        context.Services.AddSingleton<IToolsClient>(new ToolsApiClient(http));
        context.Services.AddSingleton<IParametersClient>(new ParametersApiClient(http));
        context.Services.AddSingleton<ISecretsClient>(new SecretsApiClient(http));
        return context;
    }

    private sealed class NavigationContext(ResourceScopeRef scope) : IConsoleNavigationContext
    {
        public string ScopeRef => scope.Value;
        public string AddToUri(string uri) => uri;
    }

    private sealed class ApiHandler : HttpMessageHandler
    {
        public ApiHandler(string? inputSchema = null)
        {
            if (inputSchema is not null) Source = Tool("source.v2.4.0", "2.4.0", inputSchema);
        }
        public ToolResource Source { get; } = Tool("source.v2.4.0", "2.4.0", """{"type":"object","properties":{"store":{"type":"object","properties":{"id":{"type":"string"}}},"query":{"type":"string"}},"required":["query"]}""");
        public ToolResource Incompatible { get; } = Tool("source.v3.0.0", "3.0.0", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""");
        public int Publications { get; private set; }
        public PutToolDefinitionRequest? Saved { get; private set; }
        public List<string> Writes { get; } = [];
        public IReadOnlyList<ToolArgumentBinding> InitialBindings { get; init; } = [new LiteralToolArgumentBinding { Path = "/store", Value = JsonSerializer.SerializeToElement(new { id = "hr" }) }];
        public ParameterResource Parameter { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ParameterResourceKinds.Parameter,
            Metadata = new() { Name = "query", Namespace = ResourceNamespace.Parse("shared") },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new() { DisplayName = "Query", ValueType = ParameterValueType.Text, Value = JsonSerializer.SerializeToElement("hr") }
        };
        public SecretResponse Secret { get; } = new(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Secret,
            Metadata = new() { Name = "query", Namespace = ResourceNamespace.Parse("protected") },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new() { DisplayName = "Query", Vault = new("local", ResourceScopeRef.Instance), Key = "query" }
        }, "configured", true);
        public ParameterResource? CreatedParameter { get; private set; }
        public int ParameterReads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get) Writes.Add(path);
            if (path == "/api/tools") return Ok(new { value = new[] { Source, Incompatible } });
            if (path == "/api/parameters")
            {
                if (request.Method == HttpMethod.Post)
                {
                    var create = await request.Content!.ReadFromJsonAsync<CreateParameterRequest>(cancellationToken);
                    CreatedParameter = new()
                    {
                        ApiVersion = ResourceApiVersions.CoreV1,
                        Kind = ParameterResourceKinds.Parameter,
                        Metadata = new() { Name = create!.Name },
                        ScopeRef = create.ScopeRef,
                        Definition = create.Properties
                    };
                    return Ok(CreatedParameter);
                }
                ParameterReads++;
                return Ok(CreatedParameter is { } created ? new[] { Parameter, created } : [Parameter]);
            }
            if (path == "/api/secrets") return Ok(new[] { Secret });
            var properties = Saved?.Properties ?? new RelayToolDefinitionProperties
            {
                DisplayName = "HR relay",
                Version = "1.2.0",
                Target = new() { Name = Source.Name, ScopeRef = Scope, Uid = Source.Uid, Generation = Source.Generation },
                Bindings = InitialBindings
            };
            if (path.EndsWith("/versions", StringComparison.Ordinal))
            {
                var version = new ToolDefinitionVersionResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ToolResourceKinds.ToolDefinitionVersion,
                    Metadata = new() { Name = "relay.v1.2.0" },
                    ScopeRef = Scope,
                    DefinitionName = "relay",
                    DefinitionUid = Guid.NewGuid(),
                    Definition = properties,
                    PublishedAt = DateTimeOffset.UnixEpoch,
                    Source = new(Source, Provider())
                };
                if (request.Method == HttpMethod.Post) { Publications++; return Ok(version); }
                return Ok(new { value = new[] { version } });
            }
            if (path.EndsWith("/usages", StringComparison.Ordinal)) return Ok(new { value = Array.Empty<ToolDefinitionUsage>() });
            if (request.Method == HttpMethod.Put) Saved = await request.Content!.ReadFromJsonAsync<PutToolDefinitionRequest>(cancellationToken);
            return Ok(new ToolDefinitionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolDefinition,
                Metadata = new() { Name = "relay" },
                ScopeRef = Scope,
                Definition = Saved?.Properties ?? properties
            });
        }
        private static HttpResponseMessage Ok<T>(T value)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
            response.Headers.ETag = new("\"1\"");
            return response;
        }
        private static ToolResource Tool(string name, string version, string schema) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new() { Name = name },
            ScopeRef = Scope,
            Uid = Guid.NewGuid(),
            Generation = 1,
            Definition = new()
            {
                DisplayName = name,
                Version = version,
                Schema = new() { Input = JsonSerializer.Deserialize<JsonElement>(schema) },
                Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }
            }
        };
        private static ToolProviderResource Provider() => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new() { Name = "provider" },
            ScopeRef = Scope,
            Definition = new() { DisplayName = "Provider", ProviderType = ToolProviderType.Mcp }
        };
    }
}
