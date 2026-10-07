using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
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
            var profile = rendered.Find("[data-testid='knowledge-source-profile']");
            Assert.IsTrue(profile.HasAttribute("required"));
            Assert.AreEqual("Select a profile", profile.QuerySelector("option")?.TextContent);
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-configuration']"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-editor-enabled-option'].knowledge-choice"));
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
    public void CreationSerializesTheTemporarySourceConfigurationJson()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new KnowledgeClientStub();
        using var context = CreateContext(client);
        var rendered = context.Render<KnowledgeSourceEditor>();

        rendered.WaitForAssertion(() => Assert.HasCount(1,
            rendered.FindAll("[data-testid='knowledge-source-configuration']")));
        rendered.Find("[data-testid='knowledge-source-display-name']").Change("REST documentation");
        rendered.Find("[data-testid='knowledge-source-profile']").Change("default|web");
        rendered.Find("[data-testid='knowledge-source-configuration']")
            .Change("{\"url\":\"https://docs.agentstration.io\"}");
        rendered.Find("button[type='submit']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(client.CreatedRequest);
            Assert.AreEqual("https://docs.agentstration.io",
                client.CreatedRequest.Properties.AcquisitionConfiguration.GetProperty("url").GetString());
        });
    }

    [TestMethod]
    public void CreationRejectsAConfigurationThatIsNotAJsonObject()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new KnowledgeClientStub();
        using var context = CreateContext(client);
        var rendered = context.Render<KnowledgeSourceEditor>();

        rendered.WaitForAssertion(() => Assert.HasCount(1,
            rendered.FindAll("[data-testid='knowledge-source-configuration']")));
        rendered.Find("[data-testid='knowledge-source-display-name']").Change("Invalid source");
        rendered.Find("[data-testid='knowledge-source-profile']").Change("default|web");
        rendered.Find("[data-testid='knowledge-source-configuration']").Change("[]");
        rendered.Find("button[type='submit']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNull(client.CreatedRequest);
            StringAssert.Contains(rendered.Find("[role='alert']").TextContent,
                "Source configuration must be a JSON object.");
        });
    }

    [TestMethod]
    public void EditionKeepsTechnicalNameVisibleAndImmutable()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub(ExistingSource()));
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
        var existing = ExistingSource();
        existing = existing with
        {
            Definition = existing.Definition with
            {
                AcquisitionConfiguration = JsonSerializer.SerializeToElement(new
                {
                    url = "https://docs.agentstration.io"
                })
            }
        };
        var client = new KnowledgeClientStub(existing);
        using var context = CreateContext(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/knowledge-sources/agentstration-documentation/edit?namespace=default");
        var rendered = context.Render<KnowledgeSourceEditor>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() => Assert.HasCount(1,
            rendered.FindAll("[data-testid='knowledge-source-configuration']")));
        rendered.Find("button[type='submit']").Click();

        rendered.WaitForAssertion(() => Assert.AreEqual("https://docs.agentstration.io",
            client.UpdatedRequest?.Properties.AcquisitionConfiguration.GetProperty("url").GetString()));
    }

    [TestMethod]
    public void DetailsOrganizesTheLifecycleIntoAccessibleTabs()
    {
        using var culture = new TestCultureScope("en-US");
        var acquisition = ExistingAcquisition();
        using var context = CreateContext(new KnowledgeClientStub(ExistingSource(), [acquisition], ExistingExposure()));

        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() =>
        {
            var tabs = rendered.FindAll("[role='tab']");
            Assert.HasCount(7, tabs);
            CollectionAssert.AreEqual(
                new[] { "Overview", "Definition", "Acquisitions", "Snapshots", "Retrieval", "Tools", "YAML" },
                tabs.Select(tab => tab.TextContent).ToArray());
            Assert.AreEqual("true", rendered.Find("[data-testid='knowledge-source-overview-tab']").GetAttribute("aria-selected"));
            Assert.HasCount(1, rendered.FindAll(".metric-grid"));
            Assert.IsEmpty(rendered.FindAll("[data-testid='knowledge-acquisitions']"));
            Assert.AreEqual("/flows/knowledge-ingestion-builtin", rendered.Find("[data-testid='knowledge-ingestion-flow-link']").GetAttribute("href"));
            Assert.AreEqual("/flows/knowledge-retrieval-builtin", rendered.Find("[data-testid='knowledge-retrieval-flow-link']").GetAttribute("href"));
            Assert.IsTrue(rendered.Find("[data-testid='knowledge-ingestion-flow-link']").ClassList.Contains("knowledge-flow-card"));
            var profileLink = rendered.Find("[data-testid='knowledge-source-profile-link']");
            Assert.AreEqual("/knowledge-source-profiles/web", profileLink.GetAttribute("href"));
            StringAssert.Contains(profileLink.TextContent, "web");
            StringAssert.Contains(profileLink.TextContent, "1.0.0");
            Assert.AreEqual("Delete", rendered.Find("[data-testid='knowledge-source-delete']").TextContent);
            Assert.IsEmpty(rendered.FindAll("a[href$='/edit']"));
        });

        rendered.Find("[data-testid='knowledge-source-delete']").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-delete-dialog']")));
        rendered.Find("[data-testid='knowledge-source-delete-dialog'] .button-secondary").Click();
        rendered.WaitForAssertion(() => Assert.IsEmpty(rendered.FindAll("[data-testid='knowledge-source-delete-dialog']")));

        rendered.Find("[data-testid='knowledge-source-acquisitions-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("true", rendered.Find("[data-testid='knowledge-source-acquisitions-tab']").GetAttribute("aria-selected"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-acquisitions']"));
            Assert.IsEmpty(rendered.FindAll(".metric-grid"));
            Assert.AreEqual("knowledge-source-acquisitions-tab", rendered.Find("[role='tabpanel']").GetAttribute("aria-labelledby"));
            var flowRunLink = rendered.Find(".knowledge-flow-run-link");
            Assert.AreEqual(acquisition.FlowRunId, flowRunLink.GetAttribute("title"));
            Assert.EndsWith("…", flowRunLink.TextContent);
            Assert.AreEqual("Attempt 1", rendered.Find(".knowledge-acquisition-identity small").TextContent);
            var contract = rendered.Find("[data-testid='knowledge-acquisition-contract-fields']").TextContent;
            StringAssert.Contains(contract, "location");
            StringAssert.Contains(contract, "Required");
            StringAssert.Contains(contract, "Type: string");
            StringAssert.Contains(contract, "Location to acquire");
        });

        rendered.Find("[data-testid='knowledge-acquisition-parameters']").Input("not-json");
        rendered.WaitForAssertion(() =>
        {
            Assert.IsTrue(rendered.Find("[data-testid='knowledge-start-acquisition']").HasAttribute("disabled"));
            Assert.HasCount(1, rendered.FindAll(".field-validation-error"));
        });
        rendered.Find("[data-testid='knowledge-acquisition-parameters']").Input("{\"location\":\"docs\"}");
        rendered.WaitForAssertion(() =>
        {
            Assert.IsFalse(rendered.Find("[data-testid='knowledge-start-acquisition']").HasAttribute("disabled"));
            Assert.IsEmpty(rendered.FindAll(".field-validation-error"));
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
    public void DetailsEditsTheDefinitionAndShowsTheSavedYaml()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new KnowledgeClientStub(ExistingSource());
        using var context = CreateContext(client);

        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() => Assert.HasCount(7, rendered.FindAll("[role='tab']")));
        rendered.Find("[data-testid='knowledge-source-definition-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            var technicalName = rendered.Find("[data-testid='knowledge-source-definition-name']");
            Assert.AreEqual("agentstration-documentation", technicalName.GetAttribute("value"));
            Assert.IsTrue(technicalName.HasAttribute("readonly"));
            Assert.IsTrue(technicalName.HasAttribute("disabled"));
            var profileOption = rendered.Find("[data-testid='knowledge-source-definition-profile'] option[value='default|web']");
            Assert.AreEqual("Web (1.0.0)", profileOption.TextContent);
            Assert.IsFalse(profileOption.TextContent.Contains("??", StringComparison.Ordinal));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-definition-configuration']"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-enabled-option'].knowledge-choice"));
        });

        rendered.Find("[data-testid='knowledge-source-definition-display-name']").Change("Updated documentation");
        rendered.Find("[data-testid='knowledge-source-definition-configuration']")
            .Change("{\"url\":\"https://docs.agentstration.io\"}");
        rendered.Find("[data-testid='knowledge-source-definition-save']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(client.UpdatedRequest);
            Assert.AreEqual("Updated documentation", client.UpdatedRequest.Properties.DisplayName);
            Assert.AreEqual("https://docs.agentstration.io",
                client.UpdatedRequest.Properties.AcquisitionConfiguration.GetProperty("url").GetString());
            Assert.AreEqual("web", client.UpdatedRequest.Properties.Profile?.Name);
            Assert.IsNull(client.UpdatedRequest.Properties.IngestionFlow);
            Assert.IsNull(client.UpdatedRequest.Properties.RetrievalFlow);
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
        using var context = CreateContext(new KnowledgeClientStub(ExistingSource()));

        var rendered = context.Render<KnowledgeSources>();

        rendered.WaitForAssertion(() =>
        {
            var link = rendered.Find("[data-testid='knowledge-source-name-link']");
            Assert.AreEqual("Agentstration documentation", link.TextContent);
            Assert.AreEqual("/knowledge-sources/agentstration-documentation", link.GetAttribute("href"));
        });
    }

    [TestMethod]
    public void ProfileDetailsUseCardsChoicesAndTheFullWidthYamlEditor()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub(), new KnowledgeSourceProfilesClientStub(ExistingProfile()));

        var rendered = context.Render<KnowledgeSourceProfileDetails>(parameters => parameters
            .Add(component => component.Name, "web"));

        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(2, rendered.FindAll(".profile-flow-card"));
            Assert.AreEqual("/flows/knowledge-ingestion-builtin", rendered.Find("[data-testid='profile-ingestion-flow']").GetAttribute("href"));
            Assert.HasCount(3, rendered.FindAll(".profile-composition-stats article"));
        });

        rendered.FindAll("[role='tab']").Single(tab => tab.TextContent == "Definition").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='profile-enabled-option'].profile-choice")));

        rendered.FindAll("[role='tab']").Single(tab => tab.TextContent == "Revisions").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='profile-activate-option'].profile-choice")));

        rendered.FindAll("[role='tab']").Single(tab => tab.TextContent == "YAML").Click();
        rendered.WaitForAssertion(() =>
        {
            var editor = rendered.Find("[data-testid='knowledge-source-profile-yaml-editor']");
            Assert.AreEqual("28", editor.GetAttribute("rows"));
            Assert.IsNotNull(editor.Closest(".profile-yaml-field"));
            StringAssert.Contains(editor.GetAttribute("value") ?? editor.TextContent,
                "agentstration.io/builtin: \"true\"");
            Assert.IsTrue(rendered.FindAll("button").Any(button => button.TextContent == "Apply to form"));
        });

        rendered.FindAll("button").Single(button => button.TextContent == "Apply to form").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(0, rendered.FindAll(".form-alert-danger"));
            StringAssert.Contains(rendered.Markup, "The YAML definition was applied to the form without being saved.");
        });
    }

    private static BunitContext CreateContext(IKnowledgeSourcesClient knowledge, IKnowledgeSourceProfilesClient? profiles = null)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(knowledge);
        context.Services.AddSingleton(profiles ?? new KnowledgeSourceProfilesClientStub());
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub());
        return context;
    }

    private static KnowledgeSourceResource ExistingSource() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeSource,
        Metadata = new() { Name = "agentstration-documentation" },
        ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
        Definition = new()
        {
            DisplayName = "Agentstration documentation",
            Profile = new("web")
        }
    };

    private static KnowledgeSourceProfileResource ExistingProfile() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeSourceProfile,
        Metadata = new()
        {
            Name = "web",
            Annotations = new Dictionary<string, string>
            {
                [ResourceProvenanceAnnotations.BuiltIn] = "true"
            }
        },
        ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
        ActiveVersion = "1.0.0",
        Definition = new()
        {
            DisplayName = "Web",
            Version = "1.0.0",
            IngestionFlow = new() { Name = "knowledge-ingestion-builtin" },
            RetrievalFlow = new() { Name = "knowledge-retrieval-builtin" }
        }
    };

    private static ResolvedKnowledgeSourceProfile ExistingResolvedProfile() => new()
    {
        Name = "web",
        Namespace = ResourceNamespace.Default,
        Uid = Guid.NewGuid(),
        Generation = 1,
        Version = "1.0.0",
        DefinitionHash = "profile-definition-hash",
        ConfigurationSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        IngestionFlow = new ResolvedKnowledgeFlowBinding("knowledge-ingestion-builtin", ResourceNamespace.Default, "1.0.0", true, IngestionInputSchema(), null, KnowledgeFlowContracts.Ingestion),
        RetrievalFlow = new ResolvedKnowledgeFlowBinding("knowledge-retrieval-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Retrieval)
    };

    private static KnowledgeAcquisitionResource ExistingAcquisition() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeAcquisition,
        Metadata = new() { Name = "acquisition-b70e6bffe11a4af291349d8ef86e5166" },
        KnowledgeSourceUid = Guid.NewGuid(),
        KnowledgeSourceName = "agentstration-documentation",
        KnowledgeSourceNamespace = ResourceNamespace.Default,
        KnowledgeSourceGeneration = 1,
        IngestionFlow = new ResolvedKnowledgeFlowBinding("knowledge-ingestion-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Ingestion),
        FlowRunId = "flowrun-knowledge-b70e6bffe11a4af291349d8ef86e5166",
        State = KnowledgeAcquisitionState.Succeeded,
        CorrelationId = "correlation-1",
        RequestHash = "request-hash",
        CreatedBy = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow
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

    private sealed class KnowledgeClientStub(KnowledgeSourceResource? source = null, IReadOnlyList<KnowledgeAcquisitionResource>? acquisitions = null, KnowledgeSourceToolExposureResource? exposure = null) : IKnowledgeSourcesClient
    {
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
                new ResolvedKnowledgeFlowBinding("knowledge-ingestion-builtin", ResourceNamespace.Default, "1.0.0", true, IngestionInputSchema(), null, KnowledgeFlowContracts.Ingestion),
                new ResolvedKnowledgeFlowBinding("knowledge-retrieval-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Retrieval),
                [],
                ExistingResolvedProfile()));
        public Task<KnowledgeSourceToolExposureResource?> GetExposureAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult(exposure);
        public Task<ResourceSnapshot<KnowledgeSourceToolExposureResource>> PublishExposureAsync(ResourceNamespace @namespace, string name, PublishKnowledgeSourceToolExposureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeAcquisitionResource>> GetAcquisitionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult(acquisitions ?? []);
        public Task<ResourceSnapshot<KnowledgeAcquisitionResource>> StartAcquisitionAsync(ResourceNamespace @namespace, string name, StartKnowledgeAcquisitionRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeAcquisitionResource>> CancelAcquisitionAsync(ResourceNamespace @namespace, string acquisitionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeAcquisitionResource>> RetryAcquisitionAsync(ResourceNamespace @namespace, string acquisitionId, RetryKnowledgeAcquisitionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeSnapshotView>> GetSnapshotsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KnowledgeSnapshotView>>([]);
        public Task<KnowledgeSnapshotView> SelectActiveSnapshotAsync(ResourceNamespace @namespace, string name, string snapshotName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSnapshotResource>> PublishSnapshotAsync(ResourceNamespace @namespace, string acquisitionId, PublishKnowledgeSnapshotRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeRetrievalResult> SearchAsync(ResourceNamespace @namespace, string name, SearchKnowledgeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeRetrievalResult> QueryAsync(ResourceNamespace @namespace, string name, QueryKnowledgeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeRetrievalResult> ReadAsync(ResourceNamespace @namespace, string name, ReadKnowledgeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static JsonElement IngestionInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            parameters = new
            {
                type = "object",
                properties = new { location = new { type = "string", description = "Location to acquire" } },
                required = new[] { "location" }
            }
        }
    });

    private sealed class FlowClientStub : IFlowApiClient
    {
        public Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FlowSummary>>(
        [
            new("knowledge-ingestion-builtin", "Knowledge ingestion", "flow", "1.0.0", "Published", 1, 0, DateTimeOffset.UtcNow) { ActiveVersion = "1.0.0" },
            new("knowledge-retrieval-builtin", "Knowledge retrieval", "flow", "1.0.0", "Published", 1, 0, DateTimeOffset.UtcNow) { ActiveVersion = "1.0.0" }
        ]);
        public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
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

    private sealed class KnowledgeSourceProfilesClientStub(KnowledgeSourceProfileResource? profile = null) : IKnowledgeSourceProfilesClient
    {
        public Task<IReadOnlyList<KnowledgeSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeSourceProfileResource>>([profile ?? ExistingProfile()]);
        public Task<ResourceSnapshot<KnowledgeSourceProfileResource>> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceSnapshot<KnowledgeSourceProfileResource>(profile ?? ExistingProfile(), "\"etag-profile\""));
        public Task<IReadOnlyList<KnowledgeSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeSourceProfileRevisionResource>>([]);
        public Task<ResourceSnapshot<KnowledgeSourceProfileResource>> CreateAsync(CreateKnowledgeSourceProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSourceProfileResource>> UpdateAsync(ResourceNamespace @namespace, string name, PutKnowledgeSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSourceProfileRevisionResource>> PublishAsync(ResourceNamespace @namespace, string name, PublishKnowledgeSourceProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSourceProfileResource>> ActivateAsync(ResourceNamespace @namespace, string name, ActivateKnowledgeSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeSourceProfileApplicationPlan> PreviewApplicationAsync(ResourceNamespace @namespace, string name, PreviewKnowledgeSourceProfileApplicationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSourceProfileRevisionResource>> ApplyAsync(ResourceNamespace @namespace, string name, PreviewKnowledgeSourceProfileApplicationRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
