using System.Text;
using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class ArtifactAdministrationComponentTests
{
    [TestMethod]
    public void ArtifactIdentityCopiesTheCompleteIdentifierOutsideTheNavigationLink()
    {
        using var context = CreateContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        const string identifier = "artifact-0123456789abcdef0123456789abcdef";
        var rendered = context.Render<Agentstration.Web.Components.Pages.ArtifactReference>(parameters => parameters
            .Add(value => value.Name, "Report")
            .Add(value => value.Identifier, identifier)
            .Add(value => value.IdentifierLabel, "Identifier")
            .Add(value => value.Href, "/artifacts/staged/" + identifier));
        Assert.AreEqual(identifier, rendered.Find(".copyable-value code").TextContent);
        Assert.HasCount(0, rendered.FindAll("a .copy-value"));
        Assert.IsNotNull(rendered.Find(".resource-icon svg path"));
        Assert.AreEqual("/artifacts/staged/" + identifier, rendered.Find("a").GetAttribute("href"));
    }

    [TestMethod]
    [DataRow("/instance")]
    [DataRow("/tenants/11111111-1111-1111-1111-111111111111")]
    public void ArtifactDetailsKeepTheAdministrativeContextInContentAndDownloadLinks(string scopeRef)
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("downloadable");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);
        context.Services.AddSingleton<IConsoleNavigationContext>(new ArtifactNavigationContext(scopeRef));
        var durable = context.Render<Agentstration.Web.Components.Pages.FlowRunArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));
        SelectSection(context, "content");
        durable.WaitForAssertion(() => Assert.Contains("context=" + Uri.EscapeDataString(scopeRef),
            durable.Find("[data-testid=durable-artifact-download]").GetAttribute("href")!));
        Assert.AreEqual(ResourceScopeRef.Parse(scopeRef), durable.FindComponent<Agentstration.Web.Components.Pages.DurableArtifactContent>().Instance.ContextScope);
        var staged = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));
        SelectSection(context, "content");
        staged.WaitForAssertion(() => Assert.Contains("context=" + Uri.EscapeDataString(scopeRef),
            staged.Find("[data-testid=staged-artifact-download]").GetAttribute("href")!));
    }

    [TestMethod]
    [DataRow("en-US", "Input Artifact", "Processing execution")]
    [DataRow("fr-FR", "Artefact d’entrée", "Exécution du traitement")]
    public void TransformedArtifactsLinkTheirExactInputAndProcessingRun(string language, string inputLabel, string processingLabel)
    {
        using var culture = new TestCultureScope(language);
        using var context = CreateContext();
        var client = new ArtifactClient("redacted");
        var inputId = Guid.NewGuid().ToString("N");
        client.Durable = client.Durable with
        {
            Receipt = client.Durable.Receipt with
            {
                StorageFlowRunId = string.Empty,
                Provenance = new Dictionary<string, string>
                {
                    ["inputArtifactId"] = inputId,
                    ["inputArtifactKind"] = "durable",
                    ["processingRunId"] = "flowrun-processing",
                    ["inputSha256"] = new string('a', 64)
                }
            }
        };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);
        var rendered = context.Render<Agentstration.Web.Components.Pages.FlowRunArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));
        rendered.WaitForAssertion(() => Assert.Contains(inputLabel, rendered.Markup));
        Assert.Contains(processingLabel, rendered.Markup);
        Assert.AreEqual("/artifacts/durable/" + inputId, rendered.Find("[data-testid='artifact-transformation-input']").GetAttribute("href"));
        Assert.AreEqual("/flow-runs/flowrun-processing", rendered.Find("[data-testid='artifact-processing-run']").GetAttribute("href"));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult("http://localhost/#overview");
        module.Setup<string>("navigate", _ => true).SetResult("http://localhost/#overview");
        return context;
    }

    private static void SelectSection(BunitContext context, string section)
    {
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#" + section);
    }

    private sealed class ArtifactNavigationContext(string scopeRef) : IConsoleNavigationContext
    {
        public string? ScopeRef => scopeRef;
        public string AddToUri(string uri) => uri;
    }

    [TestMethod]
    [DataRow("en-US", "Producing resource", "Content key")]
    [DataRow("fr-FR", "Ressource productrice", "Clé du contenu")]
    public void StoredArtifactShowsItsProducerAndExactStorageWithoutInventingAStagedSource(string cultureName,
        string producerLabel, string keyLabel)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = CreateContext();
        var client = new ArtifactClient("produced content");
        var owner = ResourceScopeRef.Workspace(client.Durable.WorkspaceId.Value);
        var storage = new StorageRevisionReference(new(owner, ResourceNamespace.Default, "Storage", "output-storage"),
            Guid.NewGuid(), 3, "binding-digest");
        var producer = new ScopedResourceAddress(owner, ResourceNamespace.Default, "DataSource", "source-alpha");
        var location = new ArtifactStorageMaterialization(client.Durable.WorkspaceId,
            new(storage, new(owner, producer.Address), new()), "datasets/alpha/acquisition.json", "version-exact",
            client.Durable.Receipt.Length, client.Durable.Receipt.Sha256);
        client.Durable = client.Durable with
        {
            SourceArtifactId = null,
            ProducerResource = producer,
            ProducerExecutionId = "acquisition-alpha",
            Receipt = client.Durable.Receipt with
            { StorageFlowRunId = string.Empty, Materialization = location }
        };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);
        var rendered = context.Render<Agentstration.Web.Components.Pages.FlowRunArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));
        rendered.WaitForAssertion(() => Assert.Contains(producerLabel, rendered.Markup));
        Assert.Contains("/data-sources/source-alpha", rendered.Find("[data-testid=artifact-producer-resource]").GetAttribute("href")!);
        Assert.Contains("/storages/output-storage", rendered.Find("[data-testid=artifact-storage-link]").GetAttribute("href")!);
        Assert.Contains("acquisition-alpha", rendered.Markup);
        Assert.IsEmpty(rendered.FindAll("a[href^='/artifacts/staged/']"));
        SelectSection(context, "storage");
        Assert.Contains(keyLabel, rendered.Markup);
        Assert.Contains("datasets/alpha/acquisition.json", rendered.Markup);
        Assert.Contains("version-exact", rendered.Markup);
        var list = context.Render<Agentstration.Web.Components.Pages.Artifacts>();
        list.WaitForElement("[data-testid=durable-artifact-row]");
        Assert.Contains("source-alpha", list.Markup);
        Assert.Contains("output-storage", list.Markup);
    }

    [TestMethod]
    public void StagedArtifactContentIsOnlyReadAfterExplicitActionAndUsesResourceSections()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("governed preview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(0, client.ContentReadCount);
            Assert.AreEqual(3, rendered.FindAll(".resource-navigation-rail a[data-resource-section]").Count);
        });

        SelectSection(context, "content");
        rendered.Find("[data-testid='staged-artifact-read-preview']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, client.ContentReadCount);
            StringAssert.Contains(rendered.Markup, "governed preview");
            Assert.IsNotNull(rendered.Find("[data-testid='staged-artifact-content-scroll']"));
        });
    }

    [TestMethod]
    public void StagedArtifactCanLoadCompleteContentInBoundedChunks()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        const string expected = "complete governed artifact content";
        var client = new ArtifactClient(expected, maximumBackendChunk: 8);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForAssertion(() =>
            Assert.AreEqual(0, client.ContentReadCount));

        SelectSection(context, "content");
        rendered.Find("[data-testid='staged-artifact-read-full']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsTrue(client.ContentReadCount > 1);
            Assert.AreEqual(expected, rendered.Find("[data-testid='staged-artifact-content']").TextContent);
            StringAssert.Contains(rendered.Find("[data-testid='staged-artifact-content-status']").TextContent, "Complete content loaded");
        });
    }

    [TestMethod]
    public void StagedArtifactDisablesInlineFullContentWhenArtifactIsTooLarge()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("preview", declaredLength: 4L * 1024 * 1024 + 1);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForElement("a[data-resource-section=overview]");
        SelectSection(context, "content");

        var fullContentButton = rendered.Find("[data-testid='staged-artifact-read-full']");
        Assert.IsTrue(fullContentButton.HasAttribute("disabled"));
        Assert.IsNotNull(rendered.Find("[data-testid='staged-artifact-full-content-limit']"));
        Assert.AreEqual(0, client.ContentReadCount);
    }

    [TestMethod]
    public void StagedArtifactMovesDeleteToHeaderAndRequiresALaterRetentionDate()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("preview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForElement("[data-testid='staged-artifact-delete']");
        Assert.AreEqual("Delete", rendered.Find("[data-testid='staged-artifact-delete']").TextContent.Trim());

        SelectSection(context, "retention");
        var extend = rendered.FindAll("button").Single(value => value.TextContent.Trim() == "Extend retention");
        Assert.IsTrue(extend.HasAttribute("disabled"));
        StringAssert.Contains(rendered.Markup, "Current expiration");

        rendered.Find("input[type='datetime-local']").Change(client.Artifact.ExpiresAt.ToLocalTime().AddHours(1).ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse(rendered.FindAll("button").Single(value => value.TextContent.Trim() == "Extend retention").HasAttribute("disabled"));
    }

    [TestMethod]
    public void ArtifactListUsesReadableClickableReferencesForStagedAndDurableArtifacts()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("preview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.Artifacts>();
        rendered.WaitForElement("[data-testid='durable-artifact-row']");
        Assert.IsNotNull(rendered.Find($"a[href='/artifacts/durable/{client.Durable.ArtifactId}']"));
        StringAssert.Contains(rendered.Markup, "index.html");
        Assert.AreEqual(client.Durable.ArtifactId.ToString(),
            rendered.Find("[data-testid='durable-artifact-row'] .collection-identity-cell .copyable-value code").TextContent);

        rendered.FindAll("button").Single(value => value.TextContent.Contains("Local copies", StringComparison.Ordinal)).Click();
        Assert.IsNotNull(rendered.Find($"a[href='/artifacts/staged/{client.Artifact.ArtifactId}']"));
        Assert.IsNotNull(rendered.Find("a[href='/flow-runs/flow-run-1']"));
        StringAssert.Contains(rendered.Markup, "Ready");
        StringAssert.Contains(
            rendered.Find(".artifact-status .metric-help").GetAttribute("aria-label"),
            "finalized, verified, and ready");
        StringAssert.Contains(
            rendered.Find(".artifact-status .metric-help").GetAttribute("class"),
            "metric-help-left");
        StringAssert.Contains(rendered.Markup, "index.html");
        Assert.AreEqual(client.Artifact.ArtifactId.ToString(),
            rendered.Find("[data-testid='staged-artifact-row'] .collection-identity-cell .copyable-value code").TextContent);
    }

    [TestMethod]
    public void ArtifactTerminologyExplainsAvailabilityInFrench()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = CreateContext();
        var client = new ArtifactClient("aperçu");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var list = context.Render<Agentstration.Web.Components.Pages.Artifacts>();
        list.WaitForElement("[data-testid='durable-artifact-row']");
        StringAssert.Contains(list.Markup, "Liste");
        StringAssert.Contains(list.Markup, "Copies locales");
        list.FindAll("button").Single(value => value.TextContent.Contains("Copies locales", StringComparison.Ordinal)).Click();
        StringAssert.Contains(list.Markup, "Prête");
        StringAssert.Contains(
            list.Find(".artifact-status .metric-help").GetAttribute("aria-label"),
            "finalisée, vérifiée et prête");

        var details = context.Render<Agentstration.Web.Components.Pages.FlowRunArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));
        details.WaitForElement("a[data-resource-section=content]");
        SelectSection(context, "content");
        StringAssert.Contains(details.Markup, "copie locale");
        Assert.IsFalse(details.Markup.Contains("matérialis", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void StagedArtifactDisplaysMegabytesAndKeepsTheExactByteCount()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("preview", declaredLength: 19_343);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "0.02 MB");
            StringAssert.Contains(rendered.Markup, "19,343 bytes");
        });
    }

    [TestMethod]
    public void StagedArtifactProvenanceLinksToGovernedResources()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("preview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForAssertion(() =>
        {
            Assert.IsTrue(rendered.FindAll("a[href='/flow-runs/flow-run-1']").Count >= 2);
            Assert.IsNotNull(rendered.Find("a[href='/tools/sets/filesystem']"));
            Assert.IsNotNull(rendered.Find(".artifact-overview-metrics"));
            StringAssert.Contains(rendered.Markup, "Ready");
            StringAssert.Contains(
                rendered.Find(".artifact-overview-metrics .metric-help").GetAttribute("aria-label"),
                "finalized, verified, and ready");
        });
    }

    [TestMethod]
    public void DurableArtifactHasGovernedMetadataDetails()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("preview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.FlowRunArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));

        rendered.WaitForElement("a[data-resource-section=overview]");
        StringAssert.Contains(rendered.Markup, "index.html");
        Assert.IsNotNull(rendered.Find($"a[href='/artifacts/staged/{client.Artifact.ArtifactId}']"));

        SelectSection(context, "content");
        StringAssert.Contains(rendered.Markup, "local copy");
        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(rendered.Find($"a[href='/artifacts/staged/{client.Artifact.ArtifactId}']"));
            Assert.IsNotNull(rendered.Find("[data-testid='durable-artifact-download']"));
            Assert.AreEqual("false", rendered.Find("[data-testid='durable-artifact-download']").GetAttribute("data-enhance-nav"));
            Assert.IsNotNull(rendered.Find("[data-testid='durable-artifact-materialization-history']"));
            Assert.IsNotNull(rendered.Find("a[href='/flow-runs/flowrun-materialize-1']"));
            Assert.IsNotNull(rendered.Find("[data-testid='durable-artifact-history-download']"));
            Assert.AreEqual("false", rendered.Find("[data-testid='durable-artifact-history-download']").GetAttribute("data-enhance-nav"));
            Assert.AreEqual(0, rendered.FindAll("[data-testid='durable-artifact-materialize']").Count);
        });

        SelectSection(context, "storage");
        StringAssert.Contains(rendered.Markup, "Immutable storage receipt");
        Assert.IsFalse(rendered.Markup.Contains("opaque-reference", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("en-US", "JSON records", "Read record", "Record contract")]
    [DataRow("fr-FR", "Lignes JSON", "Lire la ligne", "Contrat des lignes")]
    public void RecordMetadataNeedsNoContentReadAndExplicitFailureKeepsTheMetadata(string language, string title, string action, string contractLabel)
    {
        using var culture = new TestCultureScope(language);
        using var context = CreateContext();
        var client = new ArtifactClient("{\"id\":2}");
        var owner = ResourceScopeRef.Workspace(client.Durable.WorkspaceId.Value);
        var storage = new StorageRevisionReference(new(owner, ResourceNamespace.Default, "Storage", "records"), Guid.NewGuid(), 1, "binding");
        var target = new StorageContentRequest(storage, new(owner, new(ResourceNamespace.Default, "DataSource", "source")), new());
        var contract = Agentstration.Schemas.DataContracts.Declared(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }), "root")!;
        var publication = new StoragePublicationReference(target, "records", "version", 20, new string('a', 64))
        { Records = new([new("first", 8, new string('a', 64)), new("second", 9, new string('b', 64))]) };
        client.Durable = client.Durable with
        {
            Receipt = client.Durable.Receipt with
            {
                Materialization = new(client.Durable.WorkspaceId, target, publication.Key, publication.Version, publication.Length, publication.Sha256, publication)
                { RecordContract = contract }
            }
        };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);
        var rendered = context.Render<Agentstration.Web.Components.Pages.FlowRunArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));
        rendered.WaitForAssertion(() => Assert.Contains(title, rendered.Markup));
        Assert.Contains(contractLabel, rendered.Markup);
        Assert.AreEqual(0, client.RecordReadCount);
        Assert.AreEqual(0, client.ContentReadCount);
        var buttons = rendered.FindAll("[data-testid=artifact-records] button").Where(button => button.TextContent == action).ToArray();
        Assert.AreEqual(action, buttons[1].TextContent);
        buttons[1].Click();
        rendered.WaitForAssertion(() => Assert.Contains("2", rendered.Find("[data-testid=artifact-record-content]").TextContent));
        Assert.AreEqual("second", client.SelectedRecord);
        client.RejectRecordRead = true;
        rendered.FindAll("[data-testid=artifact-records] button").Where(button => button.TextContent == action).ToArray()[0].Click();
        rendered.WaitForElement("[role=alert]");
        Assert.Contains(title, rendered.Markup);
        Assert.Contains(contractLabel, rendered.Markup);
        Assert.AreEqual(2, rendered.FindAll("[data-testid=artifact-records] tbody tr").Count);
    }

    [TestMethod]
    public void DurableArtifactRetainsItsVisitedContentAndDoesNotReloadForSectionChanges()
    {
        using var context = CreateContext();
        var client = new ArtifactClient("retained content");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);
        var rendered = context.Render<Agentstration.Web.Components.Pages.FlowRunArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));
        rendered.WaitForElement(".resource-identity-summary");
        Assert.Contains(client.Durable.ArtifactId.ToString(), rendered.Find(".resource-identity-summary").TextContent);
        Assert.IsEmpty(rendered.FindComponents<Agentstration.Web.Components.Pages.DurableArtifactContent>());
        SelectSection(context, "content");
        var content = rendered.FindComponent<Agentstration.Web.Components.Pages.DurableArtifactContent>().Instance;
        SelectSection(context, "storage");
        Assert.IsTrue(rendered.Find("[data-artifact-section=content]").HasAttribute("hidden"));
        SelectSection(context, "content");
        Assert.AreSame(content, rendered.FindComponent<Agentstration.Web.Components.Pages.DurableArtifactContent>().Instance);
        rendered.Render(parameters => parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));
        Assert.AreEqual(1, client.DurableReadCount);
        Assert.AreEqual(0, client.ContentReadCount);
        Assert.AreEqual(0, client.RecordReadCount);
    }

    [TestMethod]
    public void StagedArtifactRetainsExpirationAndContentAcrossSectionsBeforeExtendingTheExactArtifact()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext();
        var client = new ArtifactClient("retained local content");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);
        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));
        rendered.WaitForElement(".resource-identity-summary");
        Assert.Contains(client.Artifact.ArtifactId.ToString(), rendered.Find(".resource-identity-summary").TextContent);
        Assert.IsEmpty(rendered.FindComponents<Agentstration.Web.Components.Pages.StagedArtifactContent>());
        Assert.IsEmpty(rendered.FindAll(".resource-header .resource-scope-badge"));
        SelectSection(context, "retention");
        var requested = client.Artifact.ExpiresAt.AddHours(1).ToLocalTime().ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);
        rendered.Find("input[type=datetime-local]").Change(requested);
        SelectSection(context, "content");
        var content = rendered.FindComponent<Agentstration.Web.Components.Pages.StagedArtifactContent>().Instance;
        SelectSection(context, "overview");
        Assert.IsTrue(rendered.Find("[data-artifact-section=content]").HasAttribute("hidden"));
        SelectSection(context, "retention");
        Assert.AreEqual(requested, rendered.Find("input[type=datetime-local]").GetAttribute("value"));
        Assert.AreSame(content, rendered.FindComponent<Agentstration.Web.Components.Pages.StagedArtifactContent>().Instance);
        rendered.Find(".artifact-retention-editor button.button-primary").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual(requested, client.Artifact.ExpiresAt.ToLocalTime().ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        rendered.Render(parameters => parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));
        Assert.AreEqual(1, client.StagedReadCount);
        Assert.AreEqual(1, client.RetentionWriteCount);
        Assert.AreEqual(0, client.ContentReadCount);
    }

    internal sealed class ArtifactClient : IArtifactsClient
    {
        private readonly byte[] content;
        private readonly int maximumBackendChunk;

        public ArtifactClient(string content, int maximumBackendChunk = int.MaxValue, long? declaredLength = null, string mediaType = "text/plain")
        {
            this.content = Encoding.UTF8.GetBytes(content);
            this.maximumBackendChunk = maximumBackendChunk;
            var now = DateTimeOffset.UtcNow;
            Artifact = new StagedArtifactView(
                StagedArtifactId.New(), null, "index.html", mediaType, declaredLength ?? this.content.Length, "sha256",
                StagedArtifactStatus.Sealed,
                new ArtifactProducer { Kind = ArtifactProducerKind.FlowRun, Id = "flow-run-1", FlowRunId = "flow-run-1" },
                "default", ResourceNamespace.Default, "filesystem", ResourceNamespace.Default, "1.0.0",
                now, now, now.AddHours(1), null, null, null, null, []);
            Durable = new FlowRunArtifactResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ArtifactResourceKinds.FlowRunArtifact,
                Metadata = new ResourceMetadata { Name = "22222222222222222222222222222222" },
                ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
                WorkspaceId = WorkspaceId.New(),
                ArtifactId = FlowRunArtifactId.Parse("22222222222222222222222222222222"),
                SourceArtifactId = Artifact.ArtifactId,
                ProducerFlowRunId = "flow-run-1",
                ProducerFlowStepId = "fetch",
                Receipt = new ArtifactStorageReceipt
                {
                    StorageFlowRunId = "storage-flow-run-1",
                    OpaqueReference = "opaque-reference",
                    MediaType = "text/html",
                    Length = this.content.Length,
                    Sha256 = "sha256",
                    Provenance = new Dictionary<string, string> { ["fileName"] = "index.html", ["provider"] = "filesystem" }
                },
                CreatedAt = now
            };
        }

        public StagedArtifactView Artifact { get; private set; }
        public FlowRunArtifactResource Durable { get; set; }
        public int ContentReadCount { get; private set; }
        public int DurableReadCount { get; private set; }
        public int StagedReadCount { get; private set; }
        public int RetentionWriteCount { get; private set; }
        public IReadOnlyList<FlowRunArtifactResource>? DurableCatalog { get; set; }
        public bool RejectDurableCatalog { get; set; }
        public bool HasLocalCopy { get; set; } = true;
        public int RecordReadCount { get; private set; }
        public string? SelectedRecord { get; private set; }
        public bool RejectRecordRead { get; set; }
        public Exception? DurableReadError { get; set; }
        public TaskCompletionSource<FlowRunArtifactResource?>? NextDurableRead { get; set; }

        public Task<ArtifactRecordContent> ReadRecordAsync(FlowRunArtifactId id, string memberId, CancellationToken cancellationToken = default)
        {
            RecordReadCount++;
            SelectedRecord = memberId;
            return RejectRecordRead ? throw new HttpRequestException("Unavailable")
                : Task.FromResult(new ArtifactRecordContent(memberId, 0, Convert.ToBase64String(content), true));
        }

        public Task<IReadOnlyList<StagedArtifactView>> GetStagedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StagedArtifactView>>([Artifact]);

        public Task<StagedArtifactView?> GetStagedAsync(StagedArtifactId id, CancellationToken cancellationToken = default)
        {
            StagedReadCount++;
            return Task.FromResult<StagedArtifactView?>(id == Artifact.ArtifactId ? Artifact : null);
        }

        public Task<ArtifactContentChunk> ReadContentAsync(StagedArtifactId id, long offset, int length, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(Artifact.ArtifactId, id);
            ContentReadCount++;
            var available = Math.Max(0, content.Length - checked((int)offset));
            var count = Math.Min(Math.Min(length, maximumBackendChunk), available);
            var bytes = content.AsSpan(checked((int)offset), count).ToArray();
            return Task.FromResult(new ArtifactContentChunk(
                offset,
                Convert.ToBase64String(bytes),
                offset + count >= content.Length));
        }

        public string GetDownloadUrl(StagedArtifactId id) => $"/api/artifacts/staged/{id}/download";

        public Task<StagedArtifactView> ExtendRetentionAsync(StagedArtifactId id, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(Artifact.ArtifactId, id);
            RetentionWriteCount++;
            Artifact = Artifact with { ExpiresAt = expiresAt };
            return Task.FromResult(Artifact);
        }

        public Task PurgeAsync(StagedArtifactId id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FlowRunArtifactResource>> GetDurableAsync(CancellationToken cancellationToken = default) =>
            RejectDurableCatalog ? throw new HttpRequestException("Catalogue unavailable") : Task.FromResult<IReadOnlyList<FlowRunArtifactResource>>(DurableCatalog ?? [Durable]);

        public Task<FlowRunArtifactResource?> GetDurableAsync(FlowRunArtifactId id, CancellationToken cancellationToken = default)
        {
            DurableReadCount++;
            if (NextDurableRead is { } pending) { NextDurableRead = null; return pending.Task; }
            if (DurableReadError is { } error) return Task.FromException<FlowRunArtifactResource?>(error);
            return Task.FromResult<FlowRunArtifactResource?>(id == Durable.ArtifactId ? Durable : null);
        }

        public Task<FlowRunArtifactMaterialization> StartMaterializationAsync(FlowRunArtifactId id,
            CancellationToken cancellationToken = default)
        {
            HasLocalCopy = true;
            return Task.FromResult(new FlowRunArtifactMaterialization(
                "flowrun-materialize-1", "Succeeded", Artifact.ArtifactId, StagedArtifactAvailable: true,
                StagedArtifactExpiresAt: Artifact.ExpiresAt));
        }

        public Task<IReadOnlyList<FlowRunArtifactMaterialization>> GetMaterializationsAsync(FlowRunArtifactId id,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FlowRunArtifactMaterialization>>(HasLocalCopy ? [
                new("flowrun-materialize-1", "Succeeded", Artifact.ArtifactId, StagedArtifactAvailable: true,
                    StagedArtifactExpiresAt: Artifact.ExpiresAt)
            ] : []);

        public Task<FlowRunArtifactMaterialization?> GetMaterializationAsync(FlowRunArtifactId id, string flowRunId,
            CancellationToken cancellationToken = default) => Task.FromResult<FlowRunArtifactMaterialization?>(new(
                flowRunId, "Succeeded", Artifact.ArtifactId, StagedArtifactAvailable: true,
                StagedArtifactExpiresAt: Artifact.ExpiresAt));
    }
}
