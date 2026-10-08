using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class KnowledgeSourceEditorTests
{
    [TestMethod]
    public void CreationDerivesTechnicalNameUntilTheUserOverridesIt()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub());

        var rendered = context.Render<KnowledgeSourceEditor>();

        rendered.WaitForAssertion(() =>
        {
            var fields = rendered.FindAll(".form-grid > label");
            Assert.AreEqual("knowledge-source-display-name", fields[0].QuerySelector("input")?.GetAttribute("data-testid"));
            Assert.AreEqual("knowledge-source-name", fields[1].QuerySelector("input")?.GetAttribute("data-testid"));
            Assert.IsTrue(rendered.Find("[data-testid='knowledge-source-display-name']").HasAttribute("autofocus"));
            Assert.IsTrue(rendered.Find("[data-testid='knowledge-source-projection-flow']").HasAttribute("required"));
            Assert.IsTrue(rendered.Find("[data-testid='knowledge-source-retrieval-flow']").HasAttribute("required"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-data-source-bindings']"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-editor-enabled-option'].knowledge-choice"));
            Assert.IsTrue(
                rendered.Markup.IndexOf("knowledge-source-editor-enabled-option", StringComparison.Ordinal)
                < rendered.Markup.IndexOf("knowledge-source-projection-flow", StringComparison.Ordinal));
            Assert.IsFalse(rendered.Markup.Contains("Legacy direct Flow bindings", StringComparison.Ordinal));
        });

        rendered.Find("[data-testid='knowledge-source-display-name']").Change("Documentation Générale !");
        rendered.WaitForAssertion(() =>
            Assert.AreEqual("documentation-generale", rendered.Find("[data-testid='knowledge-source-name']").GetAttribute("value")));

        rendered.Find("[data-testid='knowledge-source-name']").Change("docs-custom");
        rendered.Find("[data-testid='knowledge-source-display-name']").Change("Documentation Interne");
        rendered.WaitForAssertion(() =>
            Assert.AreEqual("docs-custom", rendered.Find("[data-testid='knowledge-source-name']").GetAttribute("value")));
    }

    [TestMethod]
    public void CreationSerializesTheDataSourceBinding()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new KnowledgeClientStub();
        using var context = CreateContext(client);
        var rendered = context.Render<KnowledgeSourceEditor>();

        rendered.WaitForAssertion(() => Assert.HasCount(1,
            rendered.FindAll("[data-testid='knowledge-source-data-source-bindings']")));
        rendered.Find("[data-testid='knowledge-source-display-name']").Change("REST documentation");
        rendered.Find("[data-testid='knowledge-source-data-source-bindings'] select").Change(
            $"{DataSourceScope.Value}|default|documentation");
        rendered.Find("[data-testid='knowledge-source-data-source-bindings'] textarea")
            .Change("{\"format\":\"markdown\"}");
        rendered.Find("[data-testid='knowledge-source-binding-maximum-age']").Change("25:30:45");
        rendered.Find("button[type='submit']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(client.CreatedRequest);
            Assert.HasCount(1, client.CreatedRequest.Properties.DataSources);
            Assert.AreEqual("documentation", client.CreatedRequest.Properties.DataSources[0].DataSource.Name);
            Assert.AreEqual("markdown", client.CreatedRequest.Properties.DataSources[0].Configuration.GetProperty("format").GetString());
            Assert.AreEqual(TimeSpan.FromHours(25) + TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(45),
                client.CreatedRequest.Properties.DataSources[0].MaximumAge);
        });
    }

    [TestMethod]
    public void CreationRejectsATransformationConfigurationThatIsNotAJsonObject()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new KnowledgeClientStub();
        using var context = CreateContext(client);
        var rendered = context.Render<KnowledgeSourceEditor>();

        rendered.WaitForAssertion(() => Assert.HasCount(1,
            rendered.FindAll("[data-testid='knowledge-source-data-source-bindings']")));
        rendered.Find("[data-testid='knowledge-source-display-name']").Change("Invalid source");
        rendered.Find("[data-testid='knowledge-source-data-source-bindings'] select").Change(
            $"{DataSourceScope.Value}|default|documentation");
        rendered.Find("[data-testid='knowledge-source-data-source-bindings'] textarea").Change("[]");
        rendered.Find("button[type='submit']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNull(client.CreatedRequest);
            StringAssert.Contains(rendered.Find("[role='alert']").TextContent,
                "Parameters must be a JSON object.");
        });
    }

    [TestMethod]
    public void EditionKeepsTechnicalNameVisibleAndImmutable()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub(ProjectedSource()));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/knowledge-sources/agentstration-documentation/edit?namespace=default");

        var rendered = context.Render<KnowledgeSourceEditor>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() =>
        {
            var technicalName = rendered.Find("[data-testid='knowledge-source-name']");
            Assert.AreEqual("agentstration-documentation", technicalName.GetAttribute("value"));
            Assert.IsTrue(technicalName.HasAttribute("readonly"));
            Assert.IsTrue(technicalName.HasAttribute("disabled"));
            Assert.IsFalse(rendered.Find("[data-testid='knowledge-source-display-name']").HasAttribute("autofocus"));
        });

        rendered.Find("[data-testid='knowledge-source-display-name']").Change("Updated documentation");
        rendered.WaitForAssertion(() =>
            Assert.AreEqual("agentstration-documentation", rendered.Find("[data-testid='knowledge-source-name']").GetAttribute("value")));
    }

    [TestMethod]
    public void EditionPreservesThePersistedSourceConfiguration()
    {
        using var culture = new TestCultureScope("en-US");
        var existing = ProjectedSource() with { Definition = ProjectedSource().Definition with
        {
            DataSources = [ProjectedSource().Definition.DataSources[0] with
                { Configuration = JsonSerializer.SerializeToElement(new { format = "markdown" }), MaximumAge = TimeSpan.FromHours(49) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3) }]
        } };
        var client = new KnowledgeClientStub(existing);
        using var context = CreateContext(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/knowledge-sources/agentstration-documentation/edit?namespace=default");
        var rendered = context.Render<KnowledgeSourceEditor>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() => Assert.HasCount(1,
            rendered.FindAll("[data-testid='knowledge-source-data-source-bindings']")));
        Assert.AreEqual("49:02:03",
            rendered.Find("[data-testid='knowledge-source-binding-maximum-age']").GetAttribute("value"));
        rendered.Find("button[type='submit']").Click();

        rendered.WaitForAssertion(() => Assert.AreEqual("markdown",
            client.UpdatedRequest?.Properties.DataSources[0].Configuration.GetProperty("format").GetString()));
        Assert.AreEqual(TimeSpan.FromHours(49) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3),
            client.UpdatedRequest?.Properties.DataSources[0].MaximumAge);
    }

    [TestMethod]
    public void DetailsOrganizesTheLifecycleIntoAccessibleTabs()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub(ProjectedSource(), exposure: ExistingExposure()));

        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() =>
        {
            var tabs = rendered.FindAll("[role='tab']");
            Assert.HasCount(7, tabs);
            CollectionAssert.AreEqual(
                new[] { "Overview", "Definition", "Projections", "Snapshots", "Retrieval", "Tools", "YAML" },
                tabs.Select(tab => tab.TextContent).ToArray());
            Assert.AreEqual("true", rendered.Find("[data-testid='knowledge-source-overview-tab']").GetAttribute("aria-selected"));
            Assert.HasCount(1, rendered.FindAll(".metric-grid"));
            Assert.IsEmpty(rendered.FindAll("[data-testid='knowledge-acquisitions']"));
            Assert.AreEqual("/flows/knowledge-projection-builtin", rendered.Find("[data-testid='knowledge-projection-flow-link']").GetAttribute("href"));
            Assert.AreEqual("/flows/knowledge-retrieval-builtin", rendered.Find("[data-testid='knowledge-retrieval-flow-link']").GetAttribute("href"));
            Assert.IsTrue(rendered.Find("[data-testid='knowledge-projection-flow-link']").ClassList.Contains("knowledge-flow-card"));
            Assert.IsEmpty(rendered.FindAll("[data-testid='knowledge-source-profile-link']"));
            Assert.AreEqual("Delete", rendered.Find("[data-testid='knowledge-source-delete']").TextContent);
            Assert.IsEmpty(rendered.FindAll("a[href$='/edit']"));
        });

        rendered.Find("[data-testid='knowledge-source-delete']").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-delete-dialog']")));
        rendered.Find("[data-testid='knowledge-source-delete-dialog'] .button-secondary").Click();
        rendered.WaitForAssertion(() => Assert.IsEmpty(rendered.FindAll("[data-testid='knowledge-source-delete-dialog']")));

        rendered.Find("[data-testid='knowledge-source-projections-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("true", rendered.Find("[data-testid='knowledge-source-projections-tab']").GetAttribute("aria-selected"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-projections']"));
            Assert.IsEmpty(rendered.FindAll(".metric-grid"));
            Assert.AreEqual("knowledge-source-projections-tab", rendered.Find("[role='tabpanel']").GetAttribute("aria-labelledby"));
        });

        rendered.Find("[data-testid='knowledge-start-projection']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.IsEmpty(rendered.FindAll(".form-alert-danger"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-projection-row']"));
        });

        rendered.Find("[data-testid='knowledge-source-snapshots-tab']").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-snapshots']")));

        rendered.Find("[data-testid='knowledge-source-retrieval-tab']").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-retrieval-verification']")));

        rendered.Find("[data-testid='knowledge-source-tools-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-tool-exposure']"));
            var toolLink = rendered.Find("[data-testid='knowledge-tool-link']");
            Assert.AreEqual("/tools/agentstration.agentstration-documentation.search", toolLink.GetAttribute("href"));
            Assert.IsTrue(toolLink.ClassList.Contains("resource-name-link"));
        });
    }

    [TestMethod]
    public void DetailsExposesSnapshotArtifactsForConsultation()
    {
        using var culture = new TestCultureScope("en-US");
        var snapshot = ExistingSnapshot();
        using var context = CreateContext(new KnowledgeClientStub(ProjectedSource(), snapshots: [snapshot]));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/knowledge-sources/agentstration-documentation?view=snapshots");

        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-snapshot-row']"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-snapshot-details']"));
            var artifactLink = rendered.Find("[data-testid='knowledge-snapshot-artifact-row'] td a");
            Assert.AreEqual("/artifacts/durable/artifact-0123456789abcdef", artifactLink.GetAttribute("href"));
            Assert.AreEqual("artifact-0123456789abcdef", artifactLink.GetAttribute("title"));
            Assert.AreEqual("artifact-0123456789abcdef", artifactLink.TextContent);
            StringAssert.Contains(rendered.Find("[data-testid='knowledge-snapshot-details']").TextContent,
                "application/json");
        });

        rendered.Find("[data-testid='knowledge-source-retrieval-tab']").Click();
        rendered.Find("[data-testid='knowledge-retrieval-operation']").Change("read");
        rendered.WaitForAssertion(() =>
        {
            var artifactOption = rendered.Find(
                "[data-testid='knowledge-retrieval-artifact'] option[value='artifact-0123456789abcdef']");
            StringAssert.Contains(artifactOption.TextContent, "application/json");
            Assert.IsTrue(rendered.Find("[data-testid='knowledge-retrieval-run']").HasAttribute("disabled"));
        });
        rendered.Find("[data-testid='knowledge-retrieval-artifact']").Change("artifact-0123456789abcdef");
        rendered.WaitForAssertion(() =>
            Assert.IsFalse(rendered.Find("[data-testid='knowledge-retrieval-run']").HasAttribute("disabled")));
    }

    [TestMethod]
    public void FrenchRetrievalUsesLocalizedCardsAndGovernedLinks()
    {
        using var culture = new TestCultureScope("fr-FR");
        var snapshot = ExistingSnapshot();
        using var context = CreateContext(new KnowledgeClientStub(ProjectedSource(), snapshots: [snapshot],
            retrievalResult: ExistingRetrievalResult(snapshot)));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/knowledge-sources/agentstration-documentation?view=retrieval");

        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("Consultation",
                rendered.Find("[data-testid='knowledge-source-retrieval-tab']").TextContent);
            Assert.AreEqual("Rechercher",
                rendered.Find("[data-testid='knowledge-retrieval-operation'] option[value='search']").TextContent);
        });
        rendered.Find("[data-testid='knowledge-retrieval-text']").Change("documentation");
        rendered.Find("[data-testid='knowledge-retrieval-run']").Click();

        rendered.WaitForAssertion(() =>
        {
            var result = rendered.Find("[data-testid='knowledge-retrieval-result']");
            StringAssert.Contains(result.TextContent, "Rechercher · 1 résultat(s)");
            Assert.AreEqual("/artifacts/durable/artifact-0123456789abcdef",
                result.QuerySelector(".knowledge-retrieval-item a")?.GetAttribute("href"));
            Assert.AreEqual("artifact-0123456789abcdef",
                result.QuerySelector(".knowledge-retrieval-item a")?.TextContent);
            Assert.HasCount(1, result.QuerySelectorAll(".knowledge-retrieval-item pre"));
        });
    }

    [TestMethod]
    public void ProjectedDetailsEmbedTheDefinitionFormAndExposeFullWidthParameters()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new KnowledgeClientStub(ProjectedSource());
        using var context = CreateContext(client);
        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.Find("[data-testid='knowledge-source-definition-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-data-source-bindings']"));
            Assert.AreEqual("agentstration-documentation",
                rendered.Find("[data-testid='knowledge-source-name']").GetAttribute("value"));
            Assert.IsEmpty(rendered.FindAll(".knowledge-edit-action"));
        });
        rendered.Find("button[type='submit']").Click();
        rendered.WaitForAssertion(() => Assert.IsNotNull(client.UpdatedRequest));

        rendered.Find("[data-testid='knowledge-source-projections-tab']").Click();
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered
            .Find("[data-testid='knowledge-projection-parameters']")
            .ParentElement!.ClassList.Contains("knowledge-projection-parameters")));
    }

    [TestMethod]
    public void DetailsEditsTheDefinitionAndShowsTheSavedYaml()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new KnowledgeClientStub(ProjectedSource());
        using var context = CreateContext(client);

        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() => Assert.HasCount(7, rendered.FindAll("[role='tab']")));
        rendered.Find("[data-testid='knowledge-source-definition-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            var technicalName = rendered.Find("[data-testid='knowledge-source-name']");
            Assert.AreEqual("agentstration-documentation", technicalName.GetAttribute("value"));
            Assert.IsTrue(technicalName.HasAttribute("readonly"));
            Assert.IsTrue(technicalName.HasAttribute("disabled"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-data-source-bindings']"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-editor-enabled-option'].knowledge-choice"));
        });

        rendered.Find("[data-testid='knowledge-source-display-name']").Change("Updated documentation");
        rendered.Find("button[type='submit']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(client.UpdatedRequest);
            Assert.AreEqual("Updated documentation", client.UpdatedRequest.Properties.DisplayName);
            Assert.HasCount(1, client.UpdatedRequest.Properties.DataSources);
            Assert.AreEqual("knowledge-projection-builtin", client.UpdatedRequest.Properties.ProjectionFlow?.Name);
            Assert.AreEqual("knowledge-retrieval-builtin", client.UpdatedRequest.Properties.RetrievalFlow?.Name);
            StringAssert.Contains(rendered.Markup, "The Knowledge Source definition was saved.");
        });

        rendered.Find("[data-testid='knowledge-source-tools-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            var approval = rendered.Find("[data-testid='knowledge-source-approval-option']");
            Assert.IsTrue(approval.ClassList.Contains("knowledge-choice"));
            StringAssert.Contains(approval.TextContent, "Require approval for every generated Tool");
        });

        rendered.Find("[data-testid='knowledge-source-yaml-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            var yaml = rendered.Find("[data-testid='knowledge-source-yaml-editor']");
            Assert.IsFalse(yaml.HasAttribute("readonly"));
            var value = yaml.GetAttribute("value") ?? yaml.TextContent;
            StringAssert.Contains(value, "kind: KnowledgeSource");
            StringAssert.Contains(value, "displayName: Updated documentation");
        });

        var yamlEditor = rendered.Find("[data-testid='knowledge-source-yaml-editor']");
        var yamlValue = yamlEditor.GetAttribute("value") ?? yamlEditor.TextContent;
        yamlEditor.Input(yamlValue.Replace("Updated documentation", "Updated from YAML", StringComparison.Ordinal));
        rendered.Find("[data-testid='knowledge-source-yaml-save']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("Updated from YAML", client.UpdatedRequest?.Properties.DisplayName);
            var savedYaml = rendered.Find("[data-testid='knowledge-source-yaml-editor']");
            StringAssert.Contains(savedYaml.GetAttribute("value") ?? savedYaml.TextContent, "displayName: Updated from YAML");
        });
    }

    [TestMethod]
    public void ListMakesTheKnowledgeSourceNameClickable()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub(ProjectedSource()));

        var rendered = context.Render<KnowledgeSources>();

        rendered.WaitForAssertion(() =>
        {
            var link = rendered.Find("[data-testid='knowledge-source-name-link']");
            Assert.AreEqual("Agentstration documentation", link.TextContent);
            Assert.AreEqual("/knowledge-sources/agentstration-documentation", link.GetAttribute("href"));
            var headers = rendered.FindAll("th").Select(value => value.TextContent).ToArray();
            CollectionAssert.Contains(headers, "Data Sources");
            CollectionAssert.Contains(headers, "Projection Flow");
            CollectionAssert.DoesNotContain(headers, "Profile");
            CollectionAssert.DoesNotContain(headers, "Ingestion Flow");
            var dataSource = rendered.Find("[data-testid='knowledge-source-data-source-link']");
            Assert.AreEqual("documentation", dataSource.TextContent);
            StringAssert.StartsWith(dataSource.GetAttribute("href"), "/data-sources/documentation?");
            var projection = rendered.Find("[data-testid='knowledge-source-projection-flow-link']");
            Assert.AreEqual("default/knowledge-projection-builtin:1.0.0", projection.TextContent);
            Assert.AreEqual("/flows/knowledge-projection-builtin", projection.GetAttribute("href"));
        });
    }

    private static BunitContext CreateContext(IKnowledgeSourcesClient knowledge)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(knowledge);
        context.Services.AddSingleton<IDataSourcesClient>(new DataSourcesClientStub());
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub());
        return context;
    }

    private static readonly ResourceScopeRef DataSourceScope = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static KnowledgeSourceResource ProjectedSource() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeSource,
        Metadata = new() { Name = "agentstration-documentation" },
        ScopeRef = DataSourceScope,
        Definition = new()
        {
            DisplayName = "Agentstration documentation",
            ProjectionFlow = new() { Name = "knowledge-projection-builtin", Version = "1.0.0", UseActiveVersion = false },
            RetrievalFlow = new() { Name = "knowledge-retrieval-builtin", Version = "1.0.0", UseActiveVersion = false },
            DataSources = [new() { Name = "documentation", DataSource = new("documentation", DataSourceScope) }]
        }
    };

    private static KnowledgeSourceToolExposureResource ExistingExposure() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeSourceToolExposure,
        Metadata = new() { Name = "agentstration-documentation" },
        KnowledgeSourceUid = Guid.NewGuid(),
        KnowledgeSourceName = "agentstration-documentation",
        KnowledgeSourceGeneration = 1,
        RetrievalFlow = new ResolvedKnowledgeFlowBinding("knowledge-retrieval-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Retrieval),
        ToolSet = new ResourceReference("agentstration-documentation", @namespace: ResourceNamespace.Default),
        ToolSetVersion = "1.0.0",
        Operations =
        [
            new KnowledgeSourceToolOperationExposure
            {
                Operation = KnowledgeSourceOperation.Search,
                Tool = new ResourceReference("agentstration.agentstration-documentation.search", @namespace: ResourceNamespace.Default),
                Capability = KnowledgeFlowContracts.Search,
                Route = "search"
            }
        ]
    };

    private static KnowledgeSnapshotView ExistingSnapshot() => new(new KnowledgeSnapshotResource
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeSnapshot,
        Metadata = new() { Name = "snapshot-0123456789abcdef" },
        ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
        KnowledgeSourceUid = Guid.NewGuid(),
        KnowledgeSourceName = "agentstration-documentation",
        KnowledgeSourceNamespace = ResourceNamespace.Default,
        KnowledgeSourceGeneration = 1,
        ProjectionId = "projection-0123456789abcdef",
        ProjectionUid = Guid.NewGuid(),
        ProjectionFlow = new ResolvedKnowledgeFlowBinding("knowledge-projection-builtin", ResourceNamespace.Default,
            "1.0.0", true, null, null, KnowledgeFlowContracts.Projection),
        ProjectionFlowRunId = "flowrun-projection-0123456789abcdef",
        RetrievalFlow = new ResolvedKnowledgeFlowBinding("knowledge-retrieval-builtin", ResourceNamespace.Default,
            "1.0.0", true, null, null, KnowledgeFlowContracts.Retrieval),
        RequestHash = "request-hash",
        PublishedAt = DateTimeOffset.UtcNow,
        PublishedBy = Guid.NewGuid(),
        Artifacts =
        [
            new KnowledgeSnapshotArtifact
            {
                ArtifactId = "artifact-0123456789abcdef",
                ProducerFlowRunId = "flowrun-producer-0123456789abcdef",
                ProducerFlowStepId = "fetch",
                StorageFlowRunId = "flowrun-storage-0123456789abcdef",
                MediaType = "application/json",
                Length = 1536,
                Sha256 = new string('a', 64)
            }
        ]
    }, KnowledgeSnapshotLifecycleState.Active);

    private static KnowledgeRetrievalResult ExistingRetrievalResult(KnowledgeSnapshotView snapshot) => new()
    {
        Operation = KnowledgeSourceOperation.Search,
        KnowledgeSourceId = "default/agentstration-documentation",
        KnowledgeSourceUid = snapshot.Snapshot.KnowledgeSourceUid,
        KnowledgeSourceGeneration = snapshot.Snapshot.KnowledgeSourceGeneration,
        SnapshotName = snapshot.Snapshot.Name,
        SnapshotUid = snapshot.Snapshot.Uid,
        RetrievalFlow = new ResolvedKnowledgeFlowBinding("knowledge-retrieval-builtin", ResourceNamespace.Default,
            "1.0.0", true, null, null, KnowledgeFlowContracts.Retrieval),
        FlowRunId = "flowrun-retrieval-0123456789abcdef",
        CorrelationId = "retrieval-correlation",
        Items =
        [
            new KnowledgeRetrievalItem
            {
                Id = "artifact-0123456789abcdef:0",
                ArtifactId = "artifact-0123456789abcdef",
                Content = "Agentstration documentation",
                MediaType = "application/json"
            }
        ]
    };

    private sealed class KnowledgeClientStub(KnowledgeSourceResource? source = null, KnowledgeSourceToolExposureResource? exposure = null, IReadOnlyList<KnowledgeSnapshotView>? snapshots = null, KnowledgeRetrievalResult? retrievalResult = null) : IKnowledgeSourcesClient
    {
        private readonly List<KnowledgeProjectionResource> projections = [];
        public CreateKnowledgeSourceRequest? CreatedRequest { get; private set; }
        public PutKnowledgeSourceRequest? UpdatedRequest { get; private set; }
        public Task<IReadOnlyList<KnowledgeSourceResource>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeSourceResource>>(source is null ? [] : [source]);
        public Task<ResourceSnapshot<KnowledgeSourceResource>> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            source is not null ? Task.FromResult(new ResourceSnapshot<KnowledgeSourceResource>(source, "\"etag-1\"")) : throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSourceResource>> CreateAsync(CreateKnowledgeSourceRequest request, CancellationToken cancellationToken = default)
        {
            CreatedRequest = request;
            var created = new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new() { Name = request.Name, Namespace = ResourceNamespace.Parse(request.Namespace) },
                ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
                Definition = request.Properties
            };
            return Task.FromResult(new ResourceSnapshot<KnowledgeSourceResource>(created, "\"etag-created\""));
        }
        public Task<ResourceSnapshot<KnowledgeSourceResource>> UpdateAsync(ResourceNamespace @namespace, string name, PutKnowledgeSourceRequest request, string etag, CancellationToken cancellationToken = default)
        {
            UpdatedRequest = request;
            return source is not null
                ? Task.FromResult(new ResourceSnapshot<KnowledgeSourceResource>(source with { Definition = request.Properties }, "\"etag-2\""))
                : throw new NotSupportedException();
        }
        public Task<ResourceSnapshot<KnowledgeSourceResource>> SetEnabledAsync(ResourceNamespace @namespace, string name, bool enabled, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeSourceReadiness> GetReadinessAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgeSourceReadiness(true, true,
                new ResolvedKnowledgeFlowBinding("knowledge-retrieval-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Retrieval),
                [],
                new ResolvedKnowledgeFlowBinding("knowledge-projection-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Projection),
                [new("documentation", DataSourceScope, Guid.NewGuid(), true, null, null)]));
        public Task<KnowledgeSourceToolExposureResource?> GetExposureAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult(exposure);
        public Task<ResourceSnapshot<KnowledgeSourceToolExposureResource>> PublishExposureAsync(ResourceNamespace @namespace, string name, PublishKnowledgeSourceToolExposureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeProjectionResource>> GetProjectionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeProjectionResource>>(projections);
        public Task<ResourceSnapshot<KnowledgeProjectionResource>> StartProjectionAsync(ResourceNamespace @namespace, string name, StartKnowledgeProjectionRequest request, CancellationToken cancellationToken = default)
        {
            var projection = new KnowledgeProjectionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeProjection,
                Metadata = new() { Name = "projection-0123456789abcdef", Namespace = @namespace },
                ScopeRef = source?.ScopeRef,
                KnowledgeSourceUid = source?.Uid ?? Guid.NewGuid(),
                KnowledgeSourceName = name,
                KnowledgeSourceNamespace = @namespace,
                KnowledgeSourceGeneration = source?.Generation ?? 1,
                ProjectionFlow = new("knowledge-projection-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Projection),
                RetrievalFlow = new("knowledge-retrieval-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Retrieval),
                ProjectionFlowRunId = "flowrun-knowledge-0123456789abcdef",
                State = KnowledgeProjectionState.Succeeded,
                CorrelationId = "projection-0123456789abcdef",
                CreatedBy = Guid.NewGuid(),
                TenantId = Guid.NewGuid(),
                WorkspaceId = source?.ScopeRef?.TargetId ?? Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                SnapshotName = "snapshot-0123456789abcdef"
            };
            projections.Add(projection);
            return Task.FromResult(new ResourceSnapshot<KnowledgeProjectionResource>(projection, "\"projection-etag\""));
        }
        public Task<IReadOnlyList<KnowledgeSnapshotView>> GetSnapshotsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult(snapshots ?? []);
        public Task<KnowledgeSnapshotView> SelectActiveSnapshotAsync(ResourceNamespace @namespace, string name, string snapshotName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeRetrievalResult> SearchAsync(ResourceNamespace @namespace, string name, SearchKnowledgeRequest request, CancellationToken cancellationToken = default) =>
            retrievalResult is null ? throw new NotSupportedException() : Task.FromResult(retrievalResult);
        public Task<KnowledgeRetrievalResult> QueryAsync(ResourceNamespace @namespace, string name, QueryKnowledgeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeRetrievalResult> ReadAsync(ResourceNamespace @namespace, string name, ReadKnowledgeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FlowClientStub : IFlowApiClient
    {
        public Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FlowSummary>>(
        [
            new("knowledge-projection-builtin", "Knowledge projection", "flow", "1.0.0", "Published", 1, 0, DateTimeOffset.UtcNow) { ActiveVersion = "1.0.0" },
            new("knowledge-retrieval-builtin", "Knowledge retrieval", "flow", "1.0.0", "Published", 1, 0, DateTimeOffset.UtcNow) { ActiveVersion = "1.0.0" }
        ]);
        public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken)
        {
            var contract = flowId == "knowledge-projection-builtin"
                ? KnowledgeFlowContracts.Projection : KnowledgeFlowContracts.Retrieval;
            return Task.FromResult(new FlowResponse(flowId, flowId, null, "1.0.0", true, "1.0.0",
                new WorkflowFlowDefinition("input", [], [], []),
                new Dictionary<string, string> { [FlowMetadataKeys.Contract] = contract },
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
        public Task<FlowResourceSnapshot> GetFlowSnapshotAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> CreateFlowAsync(CreateFlowRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> UpdateFlowAsync(string flowId, UpdateFlowRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> CreateFlowVersionAsync(string flowId, CreateFlowVersionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<InputRequest>> GetFlowRunInputsAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InputRequest> RespondToFlowRunInputAsync(string runId, string inputId, JsonElement value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CancelFlowRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public Task<FlowDraftResponse> CreateDraftAsync(CreateFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> GetDraftAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> SaveDraftAsync(string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowValidationResponse> ValidateDraftAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowSourceResponse> GetDraftSourceAsync(string flowId, string format, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> ReplaceDraftSourceAsync(string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> PublishDraftAsync(string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CreateDraftRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> CreateDraftFromVersionAsync(string flowId, string version, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DataSourcesClientStub : IDataSourcesClient
    {
        private static readonly DataSourceResource Source = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSource,
            Metadata = new() { Name = "documentation" },
            ScopeRef = DataSourceScope,
            Definition = new()
            {
                DisplayName = "Documentation",
                Profile = new("web-builtin", DataSourceScope)
            }
        };
        public Task<IReadOnlyList<DataSourceResource>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DataSourceResource>>([Source]);
        public Task<ResourceSnapshot<DataSourceResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceResource>> CreateAsync(CreateDataSourceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PutDataSourceRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DataSourceReadiness> GetReadinessAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DataSourceAcquisitionResource>> GetAcquisitionsAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceAcquisitionResource>> StartAcquisitionAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, StartDataSourceAcquisitionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
