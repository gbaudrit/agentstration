using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceEditorTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [TestMethod]
    public void DetailsExposeOperationalOverviewAndConsistentForms()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IDataSourcesClient>(new SourceClientStub());
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}");

        var rendered = context.Render<DataSourceEditor>(parameters => parameters
            .Add(component => component.Name, "documentation"));

        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(4, rendered.FindAll(".metric-card"));
            StringAssert.Contains(rendered.Markup, "Génération");
            StringAssert.Contains(rendered.Markup, "Dernière acquisition");
            StringAssert.Contains(rendered.Markup, "Réussie");
            StringAssert.Contains(rendered.Markup, "1 acquisition(s)");
            Assert.AreEqual("data-source-web-acquisition-builtin", rendered.Find("[data-testid='data-source-acquisition-flow-link'] strong").TextContent.Trim());
            Assert.AreEqual("/flows/data-source-web-acquisition-builtin", rendered.Find("[data-testid='data-source-acquisition-flow-link']").GetAttribute("href"));
        });

        rendered.FindAll("[role='tab']").Single(value => value.TextContent == "Définition").Click();
        rendered.WaitForAssertion(() =>
        {
            var enabled = rendered.Find("[data-testid='data-source-enabled-option']");
            Assert.IsTrue(enabled.ClassList.Contains("data-source-choice"));
            Assert.IsTrue(rendered.Markup.IndexOf("data-source-enabled-option", StringComparison.Ordinal)
                < rendered.Markup.IndexOf("Configuration (JSON)", StringComparison.Ordinal));
            Assert.AreEqual("https://docs.agentstration.io",
                rendered.Find("[data-schema-path='$.url'] input").GetAttribute("value"));
        });

        rendered.FindAll("[role='tab']").Single(value => value.TextContent == "Acquisitions").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='data-source-acquisitions']"));
            Assert.HasCount(1, rendered.FindAll("[data-schema-path='$.query'] input"));
            Assert.AreEqual("Réussie", rendered.Find(".status-badge").TextContent.Trim());
            Assert.AreEqual("flowrun-data-source", rendered.Find(".data-source-flow-run").GetAttribute("title"));
        });

        rendered.FindAll("[role='tab']").Single(value => value.TextContent == "YAML").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='data-source-yaml-editor']"));
            Assert.IsTrue(rendered.Find(".data-source-yaml-field").QuerySelector("textarea") is not null);
            StringAssert.Contains(rendered.Markup, "YAML de la source de données");
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AcquisitionToolUsesSavedRecipeAndSelectsOnePublishableArtifact(bool canWrite)
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        var acquisition = Acquisition with
        {
            Target = Target,
            Parameters = JsonSerializer.SerializeToElement(new { query = "mobilité", limit = 20 }),
            Manifest = new()
            {
                Artifacts =
                [
                    new() { ArtifactId = "catalogue", Name = "catalogue.json", Kind = DataSourceArtifactKind.Durable, Disposition = DataSourceArtifactDisposition.Publishable },
                    new() { ArtifactId = "results", Name = "results.json", Kind = DataSourceArtifactKind.Staged, Disposition = DataSourceArtifactDisposition.Publishable },
                    new() { ArtifactId = "diagnostic", Name = "diagnostic.json", Kind = DataSourceArtifactKind.Staged, Disposition = DataSourceArtifactDisposition.Diagnostic }
                ]
            }
        };
        using var handler = new CreateAcquisitionToolHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://agentstration.test/") };
        var state = new ConsoleContextState(new AcquisitionConsoleContextProvider(acquisition, canWrite));
        await state.EnsureLoadedAsync(default);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton(state);
        context.Services.AddSingleton<IDataSourcesClient>(new SourceClientStub { AcquisitionResult = acquisition });
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.AddSingleton<IToolDefinitionsClient>(new ToolDefinitionsApiClient(http));
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo($"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}&view=acquisitions");

        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForElement("[data-testid='data-source-acquisition-row']");
        var linkedTool = rendered.WaitForElement("[data-testid='data-source-linked-tool']");
        StringAssert.Contains(linkedTool.GetAttribute("href"), "/tools/agentstration.saved-acquisition");
        if (!canWrite)
        {
            Assert.HasCount(0, rendered.FindAll("[data-testid='data-source-create-tool']"));
            return;
        }

        rendered.Find("[data-testid='data-source-create-tool']").Click();
        var dialog = rendered.Find("[role='dialog']");
        StringAssert.Contains(dialog.TextContent, "Créer un Tool");
        StringAssert.Contains(dialog.TextContent, acquisition.Target!.DisplayName);
        var fixedParameters = rendered.Find("[data-testid='acquisition-tool-parameters']").TextContent;
        using var parameters = JsonDocument.Parse(fixedParameters);
        Assert.AreEqual("mobilité", parameters.RootElement.GetProperty("query").GetString());
        Assert.AreEqual(20, parameters.RootElement.GetProperty("limit").GetInt32());
        var select = rendered.Find("[data-testid='acquisition-tool-artifact']");
        Assert.HasCount(3, select.QuerySelectorAll("option"));
        Assert.IsFalse(select.TextContent.Contains("diagnostic.json", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find("[data-testid='acquisition-tool-create']").HasAttribute("disabled"));

        rendered.Find("[data-testid='acquisition-tool-display-name']").Input("Catalogue mobilité");
        rendered.Find("[data-testid='acquisition-tool-name']").Input("catalogue-mobilite");
        select.Change("results");
        rendered.Find("[data-testid='acquisition-tool-editor'] form").Submit();

        rendered.WaitForAssertion(() => Assert.AreEqual(
            new CreateToolDefinitionFromAcquisitionRequest("catalogue-mobilite", "Catalogue mobilité", "results"), handler.Request));
        Assert.AreEqual("/api/tooldefinitions/from-acquisition/acquisition-1?namespace=default", handler.Path);
        var scope = Uri.EscapeDataString(Scope.Value);
        Assert.AreEqual($"/tools/agentstration.catalogue-mobilite?namespace=default&scopeRef={scope}&context={scope}&view=execution",
            new Uri(navigation.Uri).PathAndQuery);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AcquisitionOpensFormattedArtifactsOnDemandInTheSameTab(bool hasLocalCopy)
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        var json = JsonSerializer.Serialize(new { Result = new { items = new[] { 1, 2 }, text = new string('a', 47_000) } });
        var artifacts = new ArtifactAdministrationComponentTests.ArtifactClient(json, mediaType: "application/vnd.example+json")
        {
            HasLocalCopy = hasLocalCopy
        };
        var client = new SourceClientStub
        {
            AcquisitionResult = Acquisition with
            {
                Manifest = new()
                {
                    Artifacts =
                    [
                        new() { Name = "local.json", ArtifactId = artifacts.Artifact.ArtifactId.ToString(), Kind = DataSourceArtifactKind.Staged, Disposition = DataSourceArtifactDisposition.Publishable },
                        new() { Name = "durable.json", ArtifactId = artifacts.Durable.ArtifactId.ToString(), Kind = DataSourceArtifactKind.Durable, Disposition = DataSourceArtifactDisposition.Publishable },
                        new() { Name = "missing.json", ArtifactId = Guid.NewGuid().ToString("N"), Kind = DataSourceArtifactKind.Staged, Disposition = DataSourceArtifactDisposition.Publishable }
                    ]
                }
            }
        };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IDataSourcesClient>(client);
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.AddSingleton<IArtifactsClient>(artifacts);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}&view=acquisitions");

        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        const string toggle = "[data-testid='data-source-acquisition-preview']";
        const string viewer = "[data-testid='staged-artifact-content']";
        const string select = "[data-testid='data-source-acquisition-artifact-select']";
        var show = rendered.WaitForElement(toggle);
        Assert.AreEqual("Voir les artefacts", show.TextContent.Trim());
        Assert.AreEqual(0, artifacts.ContentReadCount);
        Assert.HasCount(0, rendered.FindAll(viewer));

        show.Click();
        var formatted = rendered.WaitForElement(viewer).TextContent;
        StringAssert.Contains(formatted, "\n  \"Result\": {");
        using var document = JsonDocument.Parse(formatted);
        Assert.AreEqual(2, document.RootElement.GetProperty("Result").GetProperty("items").GetArrayLength());
        Assert.AreEqual(47_000, document.RootElement.GetProperty("Result").GetProperty("text").GetString()?.Length);
        Assert.AreEqual(1, artifacts.ContentReadCount);
        StringAssert.Contains(rendered.Find("[data-testid='staged-artifact-download']").GetAttribute("href"),
            $"context={Uri.EscapeDataString(Scope.Value)}");

        rendered.Find(select).Change("1");
        if (!hasLocalCopy)
        {
            Assert.HasCount(0, rendered.FindAll(viewer));
            rendered.WaitForElement("[data-testid='durable-artifact-materialize']").Click();
        }

        Assert.AreEqual(formatted, rendered.WaitForElement(viewer).TextContent);
        Assert.AreEqual(2, artifacts.ContentReadCount);
        Assert.HasCount(1, rendered.FindAll("[data-testid='data-source-acquisition-artifacts'] a[download]"));
        Assert.HasCount(0, rendered.FindAll("[data-testid='staged-artifact-read-full'], [data-testid='staged-artifact-read-preview']"));
        Assert.HasCount(0, rendered.FindAll("[data-testid='durable-artifact-content-panel'], [data-testid='durable-artifact-materialization-history']"));

        rendered.Find(select).Change("2");
        Assert.IsNotNull(rendered.WaitForElement("[data-testid='staged-artifact-content-panel'] [role='alert']"));
        Assert.HasCount(0, rendered.FindAll(viewer));
        Assert.AreEqual(2, artifacts.ContentReadCount);

        rendered.Find(toggle).Click();
        Assert.HasCount(0, rendered.FindAll("[data-testid='data-source-acquisition-artifacts']"));
    }

    [TestMethod]
    public void AcquisitionSubmitsParametersProducedByTheResolvedFlowSchema()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new SourceClientStub();
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IDataSourcesClient>(client);
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}&view=acquisitions");

        var rendered = context.Render<DataSourceEditor>(parameters => parameters
            .Add(component => component.Name, "documentation"));

        rendered.WaitForElement("[data-schema-path='$.query'] input").Input("release notes");
        rendered.Find("button.button-primary").Click();

        rendered.WaitForAssertion(() => Assert.AreEqual("release notes",
            client.StartedRequest?.Parameters.GetProperty("query").GetString()));
    }

    [TestMethod]
    public void DiscoveryTabShowsGovernedChoicesAndStaleTargets()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IDataSourcesClient>(new SourceClientStub(targeted: true));
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}&view=discovery");

        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid='data-source-discovery-choice']"));
            StringAssert.Contains(rendered.Markup, "Documents");
            StringAssert.Contains(rendered.Markup, "Obsolète");
        });
    }

    [TestMethod]
    [DataRow("en-US", "operational status or history could not be loaded")]
    [DataRow("fr-FR", "son état opérationnel ou son historique n’a pas pu être chargé")]
    public void HistoryFailureKeepsDefinitionAndYamlLoaded(string cultureName, string expectedMessage)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IDataSourcesClient>(new SourceClientStub(historyFails: true));
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}&view=definition");
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[role=alert]").TextContent, expectedMessage);
            Assert.AreEqual("https://docs.agentstration.io", rendered.Find("[data-schema-path='$.url'] input").GetAttribute("value"));
            Assert.IsFalse(rendered.Find("fieldset").HasAttribute("disabled"));
        });
        rendered.FindAll("[role=tab]").Single(value => value.TextContent == "YAML").Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Find(".data-source-yaml-field").OuterHtml, "docs.agentstration.io"));
    }

    [TestMethod]
    public void ReadinessKeepsPinnedProfileSelectableWhenCatalogueOmitsIt()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IDataSourcesClient>(new SourceClientStub());
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub(omitProfile: true));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}&view=definition");
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual($"{Scope.Value}|default|web-builtin",
                rendered.Find("[data-testid=data-source-profile] option[selected]").GetAttribute("value"));
            Assert.AreEqual("https://docs.agentstration.io", rendered.Find("[data-schema-path='$.url'] input").GetAttribute("value"));
        });
    }

    [TestMethod]
    [DataRow("en-US", "Pending", "Running", "Succeeded")]
    [DataRow("fr-FR", "En attente", "En cours", "Réussie")]
    public async Task DiscoveryRefreshesStatesChoicesAndTargetsWithoutNavigation(
        string cultureName, string pending, string running, string succeeded)
    {
        using var culture = new TestCultureScope(cultureName);
        var clock = new ManualTimeProvider();
        var client = new SourceClientStub(targeted: true) { DiscoveryHistory = [] };
        await using var context = DiscoveryContext(client, clock);
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForElement("[data-testid=data-source-start-discovery]").Click();
        rendered.Find("[data-testid=data-source-discovery-history-tab]").Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid=data-source-discovery-row]").TextContent, pending);
            Assert.IsTrue(rendered.Find("[data-testid=data-source-start-discovery]").HasAttribute("disabled"));
            StringAssert.Contains(rendered.Find("[data-testid=data-source-discovery-flow-link]").GetAttribute("href")!, "/flows/discover?scopeRef=");
            var runLink = rendered.Find("[data-testid=data-source-discovery-row] a").GetAttribute("href")!;
            StringAssert.Contains(runLink, "scopeRef=" + Uri.EscapeDataString(Scope.Value));
            StringAssert.Contains(runLink, "context=" + Uri.EscapeDataString(ResourceScopeRef.Instance.Value));
        });

        rendered.FindAll("[role=tab]").Single(value => value.TextContent == (cultureName == "fr-FR" ? "Définition" : "Definition")).Click();
        rendered.Find("[data-schema-path='$.url'] input").Input("https://edited.example.test");
        client.DiscoveryHistory = [client.AcceptedDiscovery with { State = DataSourceDiscoveryState.Running }];
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() => Assert.AreEqual("https://edited.example.test",
            rendered.Find("[data-schema-path='$.url'] input").GetAttribute("value")));
        rendered.FindAll("[role=tab]").Single(value => value.TextContent == (cultureName == "fr-FR" ? "Découverte" : "Discovery")).Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(
            rendered.Find("[data-testid=data-source-discovery-row]").TextContent, running));

        client.DiscoveryHistory = [Discovery with { LogicalScopeRef = ResourceScopeRef.Instance }];
        client.TargetStale = false;
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid=data-source-discovery-row]").TextContent, succeeded);
            Assert.IsFalse(rendered.Find("[data-testid=data-source-start-discovery]").HasAttribute("disabled"));
            Assert.DoesNotContain(cultureName == "fr-FR" ? "Obsolète" : "Stale", rendered.Markup);
        });
        rendered.Find("[data-testid=data-source-discovery-results-tab]").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid=data-source-discovery-choice]")));
        var reads = client.DiscoveryReads;
        await rendered.InvokeAsync(clock.Tick);
        Assert.AreEqual(reads, client.DiscoveryReads, "Terminal operations must stop network polling.");
        await rendered.InvokeAsync(async () => await rendered.Instance.DisposeAsync());
        rendered.WaitForAssertion(() => Assert.IsTrue(clock.TimerDisposed));
    }

    [TestMethod]
    [DataRow("en-US", "Unable to refresh execution status.", "Failed")]
    [DataRow("fr-FR", "Impossible d’actualiser l’état des exécutions.", "Échec")]
    public async Task DiscoveryRefreshRetriesAfterFailureAndRetainsExecutionLinks(
        string cultureName, string expectedError, string failed)
    {
        using var culture = new TestCultureScope(cultureName);
        var clock = new ManualTimeProvider();
        var client = new SourceClientStub(targeted: true) { DiscoveryHistory = [] };
        await using var context = DiscoveryContext(client, clock);
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForElement("[data-testid=data-source-start-discovery]").Click();
        rendered.Find("[data-testid=data-source-discovery-history-tab]").Click();
        rendered.WaitForElement("[data-testid=data-source-discovery-row][data-state=Pending]");

        client.DiscoveryReadFails = true;
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid=data-source-operations-error]").TextContent, expectedError);
            Assert.HasCount(1, rendered.FindAll("[data-testid=data-source-discovery-row][data-state=Pending]"));
        });
        client.DiscoveryReadFails = false;
        client.DiscoveryHistory = [client.AcceptedDiscovery with { State = DataSourceDiscoveryState.Failed }];
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(0, rendered.FindAll("[data-testid=data-source-operations-error]"));
            StringAssert.Contains(rendered.Find("[data-testid=data-source-discovery-row]").TextContent, failed);
            StringAssert.Contains(rendered.Find("[data-testid=data-source-discovery-row] a").GetAttribute("href")!, "/flow-runs/flowrun-discovery?");
        });
    }

    [TestMethod]
    public async Task LeavingDiscoveryCancelsAnActiveRefresh()
    {
        var clock = new ManualTimeProvider();
        var client = new SourceClientStub(targeted: true) { DiscoveryHistory = [] };
        await using var context = DiscoveryContext(client, clock);
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForElement("[data-testid=data-source-start-discovery]").Click();
        rendered.Find("[data-testid=data-source-discovery-history-tab]").Click();
        rendered.WaitForElement("[data-testid=data-source-discovery-row][data-state=Pending]");
        client.DeferredDiscoveryRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() => Assert.IsTrue(client.ReadingDeferredDiscovery));
        await rendered.InvokeAsync(async () => await rendered.Instance.DisposeAsync());
        rendered.WaitForAssertion(() => Assert.IsTrue(clock.TimerDisposed));
        var reads = client.DiscoveryReads;
        clock.Tick();
        Assert.AreEqual(reads, client.DiscoveryReads);
    }

    [TestMethod]
    public async Task OldRefreshDoesNotReplaceDiscoveryAfterNavigation()
    {
        var clock = new ManualTimeProvider();
        var client = new SourceClientStub(targeted: true) { DiscoveryHistory = [] };
        await using var context = DiscoveryContext(client, clock);
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForElement("[data-testid=data-source-start-discovery]").Click();
        rendered.Find("[data-testid=data-source-discovery-history-tab]").Click();
        rendered.WaitForElement("[data-testid=data-source-discovery-row][data-state=Pending]");
        var oldRead = new TaskCompletionSource<IReadOnlyList<DataSourceDiscoveryResource>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DeferredDiscoveryRead = oldRead;
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() => Assert.IsTrue(client.ReadingDeferredDiscovery));
        client.DiscoveryHistory = [Discovery with { FlowRunId = "new-page-run" }];
        rendered.Render(parameters => parameters.Add(component => component.Name, "other-source"));
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Find("[data-testid=data-source-discovery-row]").TextContent, "new-page-run"));
        var renders = rendered.RenderCount;
        oldRead.SetResult([client.AcceptedDiscovery]);
        rendered.WaitForAssertion(() =>
        {
            Assert.IsTrue(rendered.RenderCount > renders);
            StringAssert.Contains(rendered.Find("[data-testid=data-source-discovery-row]").TextContent, "new-page-run");
        });
    }

    [TestMethod]
    [DataRow("en-US", "Results", "History")]
    [DataRow("fr-FR", "Résultats", "Historique")]
    public async Task DiscoveryDefaultsToResultsAndRefreshesWhileHistoryIsHidden(string cultureName, string results, string history)
    {
        using var culture = new TestCultureScope(cultureName);
        var clock = new ManualTimeProvider();
        var client = new SourceClientStub(targeted: true)
        {
            DiscoveryHistory = Enumerable.Range(0, 50).Select(index => Discovery with { Metadata = Discovery.Metadata with { Name = $"discovery-{index}" } }).ToArray()
        };
        await using var context = DiscoveryContext(client, clock);
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForElement("[data-testid=data-source-discovery-choice]");
        var resultsTab = rendered.Find("[data-testid=data-source-discovery-results-tab]");
        Assert.AreEqual(results, resultsTab.TextContent);
        Assert.AreEqual("true", resultsTab.GetAttribute("aria-selected"));
        Assert.HasCount(0, rendered.FindAll("[data-testid=data-source-discovery-row]"));
        var historyTab = rendered.Find("[data-testid=data-source-discovery-history-tab]");
        Assert.AreEqual(history, historyTab.TextContent);
        historyTab.Click();
        Assert.HasCount(50, rendered.FindAll("[data-testid=data-source-discovery-row]"));
        Assert.HasCount(0, rendered.FindAll("[data-testid=data-source-discovery-choice]"));
        Assert.AreEqual("discovery-history-tab", rendered.Find("#discovery-panel").GetAttribute("aria-labelledby"));
        rendered.Find("[data-testid=data-source-discovery-results-tab]").Click();
        rendered.Find("[data-testid=data-source-start-discovery]").Click();
        Assert.HasCount(0, rendered.FindAll("[data-testid=data-source-discovery-row]"));
        client.DiscoveryHistory = [Discovery];
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid=data-source-discovery-choice]")));
        rendered.Find("[data-testid=data-source-discovery-history-tab]").Click();
        Assert.HasCount(1, rendered.FindAll("[data-testid=data-source-discovery-row]"));
    }

    [TestMethod]
    [DataRow("en-US", "Reconcile", "Current", "documents")]
    [DataRow("fr-FR", "Réconcilier", "À jour", "catalog-documents")]
    public async Task RediscoveryReconcilesExistingTargetInsteadOfPromotingItsNameAgain(string cultureName, string reconcile, string current, string targetName)
    {
        using var culture = new TestCultureScope(cultureName);
        var client = new SourceClientStub(targeted: true);
        client.TargetResource = client.TargetResource with { Metadata = client.TargetResource.Metadata with { Name = targetName } };
        client.DiscoveryHistory = [Discovery with { Uid = Guid.NewGuid(), Metadata = Discovery.Metadata with { Name = "discovery-2" } }];
        await using var context = DiscoveryContext(client, new ManualTimeProvider());
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForElement("[data-testid=data-source-discovery-choice]");
        Assert.IsFalse(rendered.Find("[data-testid=data-source-discovery-choice] button").HasAttribute("disabled"));
        rendered.Find("[data-testid=data-source-discovery-choice] input").Input("documents-v2");
        Assert.IsFalse(rendered.Find("[data-testid=data-source-discovery-choice] button").HasAttribute("disabled"));
        rendered.Find("[data-testid=data-source-discovery-choice] input").Input(targetName);
        Assert.IsFalse(rendered.Find("[data-testid=data-source-discovery-choice] button").HasAttribute("disabled"));
        var button = rendered.Find("[data-testid=data-source-reconcile-target]");
        Assert.AreEqual(reconcile, button.TextContent);
        Assert.IsFalse(button.HasAttribute("disabled"));
        button.Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(client.Reconciled);
            Assert.AreEqual(targetName, client.Reconciled.Value.Name);
            Assert.AreEqual("discovery-2", client.Reconciled.Value.Request.DiscoveryId);
            Assert.AreEqual("documents", client.Reconciled.Value.Request.ChoiceId);
            Assert.AreEqual("\"target-v1\"", client.Reconciled.Value.ETag);
            StringAssert.Contains(rendered.Find("[data-testid=data-source-acquisition-target-row]").TextContent, current);
            var promotedButton = rendered.Find("[data-testid=data-source-discovery-choice] button");
            Assert.AreEqual(cultureName == "fr-FR" ? "Déjà promu" : "Already promoted", promotedButton.TextContent);
            Assert.IsTrue(promotedButton.HasAttribute("disabled"));
            Assert.HasCount(0, rendered.FindAll("[data-testid=data-source-reconcile-target]"));
        });
    }

    [TestMethod]
    [DataRow("en-US", "Refresh")]
    [DataRow("fr-FR", "Actualisez")]
    public async Task ReconciliationConflictKeepsStaleTargetAndReportsLocalizedRecovery(string cultureName, string recovery)
    {
        using var culture = new TestCultureScope(cultureName);
        var client = new SourceClientStub(targeted: true) { ReconcileFails = true };
        client.DiscoveryHistory = [Discovery with { Uid = Guid.NewGuid() }];
        await using var context = DiscoveryContext(client, new ManualTimeProvider());
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForElement("[data-testid=data-source-reconcile-target]").Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[role=alert]").TextContent, recovery);
            Assert.IsTrue(client.TargetStale);
            Assert.HasCount(1, rendered.FindAll("[data-testid=data-source-reconcile-target]"));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReconciliationRequiresAnEligibleChoiceInTheNewDiscovery(bool ineligible)
    {
        var client = new SourceClientStub(targeted: true);
        client.DiscoveryHistory = [Discovery with
        {
            Uid = Guid.NewGuid(),
            Manifest = new() { Choices = ineligible ? [new() { Id = "documents", DisplayName = "Documents", Eligible = false }] : [] }
        }];
        await using var context = DiscoveryContext(client, new ManualTimeProvider());
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        rendered.WaitForElement("[data-testid=data-source-acquisition-target-row]");
        if (ineligible)
            Assert.IsTrue(rendered.Find("[data-testid=data-source-reconcile-target]").HasAttribute("disabled"));
        else
            Assert.HasCount(0, rendered.FindAll("[data-testid=data-source-reconcile-target]"));
        Assert.IsNull(client.Reconciled);
    }

    private static BunitContext DiscoveryContext(SourceClientStub client, TimeProvider clock)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(clock);
        context.Services.AddSingleton<IDataSourcesClient>(client);
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}&view=discovery");
        return context;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private ManualTimer? timer;
        public bool TimerDisposed => timer?.Disposed == true;
        public void Tick() => timer?.Tick();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => timer = new ManualTimer(callback, state);

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Tick() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static DataSourceProfileProperties ProfileProperties => new()
    {
        DisplayName = "Web Builtin",
        Version = "1.0.1",
        ConfigurationSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { url = new { type = "string", title = "URL" } },
            required = new[] { "url" },
            additionalProperties = false
        }),
        AcquisitionFlow = new() { Name = "data-source-web-acquisition-builtin", Version = "1.0.1", UseActiveVersion = false }
    };

    private static JsonElement AcquisitionInputSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            parameters = new
            {
                type = "object",
                properties = new { query = new { type = "string", title = "Query" } },
                additionalProperties = false
            }
        }
    });

    private static DataSourceResource Source => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSource,
        Metadata = new() { Name = "documentation" },
        ScopeRef = Scope,
        Generation = 4,
        Definition = new()
        {
            DisplayName = "Documentation Agentstration",
            Description = "Documentation publique",
            Enabled = true,
            Profile = new("web-builtin", Scope),
            Configuration = JsonSerializer.SerializeToElement(new { url = "https://docs.agentstration.io" })
        }
    };

    private static ResolvedDataSourceProfile ResolvedProfile => new()
    {
        Name = "web-builtin",
        Namespace = ResourceNamespace.Default,
        ScopeRef = Scope,
        Uid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        Generation = 1,
        Version = "1.0.1",
        DefinitionHash = "sha256:test",
        DependencyHash = "sha256:dependencies",
        Definition = ProfileProperties,
        AcquisitionFlow = new("data-source-web-acquisition-builtin", ResourceNamespace.Default,
            "1.0.1", false, AcquisitionInputSchema, null, DataSourceFlowContracts.Acquisition,
            "sha256:flow")
        { ScopeRef = Scope },
        ToolBindings = []
    };

    private static DataSourceAcquisitionResource Acquisition => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSourceAcquisition,
        Metadata = new() { Name = "acquisition-1" },
        ScopeRef = Scope,
        DataSourceScopeRef = Scope,
        DataSourceUid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        DataSourceName = "documentation",
        DataSourceNamespace = ResourceNamespace.Default,
        DataSourceGeneration = 4,
        Composition = new()
        {
            Profile = ResolvedProfile,
            Flow = new("data-source-web-acquisition-builtin", ResourceNamespace.Default, "1.0.1", false,
                null, null, DataSourceFlowContracts.Acquisition, "sha256:flow")
            { ScopeRef = Scope }
        },
        FlowRunId = "flowrun-data-source",
        State = DataSourceAcquisitionState.Succeeded,
        CorrelationId = "correlation",
        RequestHash = "sha256:request",
        CreatedBy = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        TenantId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
        WorkspaceId = Scope.TargetId!.Value,
        CreatedAt = new DateTimeOffset(2026, 10, 8, 9, 57, 0, TimeSpan.Zero),
        CompletedAt = new DateTimeOffset(2026, 10, 8, 9, 58, 0, TimeSpan.Zero),
        Manifest = new() { Artifacts = [new() { ArtifactId = "artifact-1", Kind = DataSourceArtifactKind.Durable, Disposition = DataSourceArtifactDisposition.Publishable }] }
    };

    private sealed class AcquisitionConsoleContextProvider(DataSourceAcquisitionResource acquisition, bool canWrite) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConsoleContextSnapshot(acquisition.CreatedBy, "Administrator", acquisition.TenantId,
                "example", "Example", acquisition.WorkspaceId, "catalog", "Catalog",
                canWrite ? new HashSet<string> { AuthorizationPermissions.ResourcesWrite } : new HashSet<string>(), []));
    }

    private sealed class CreateAcquisitionToolHandler : HttpMessageHandler
    {
        public CreateToolDefinitionFromAcquisitionRequest? Request { get; private set; }
        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        value = new[]
                    {
                        new ToolDefinitionResource
                        {
                            ApiVersion = ResourceApiVersions.CoreV1,
                            Kind = ToolResourceKinds.ToolDefinition,
                            Metadata = new ResourceMetadata { Name = "saved-acquisition" },
                            ScopeRef = Scope,
                            Definition = new AcquisitionToolDefinitionProperties
                            {
                                DisplayName = "Saved acquisition",
                                Acquisition = new(Acquisition.Name, Acquisition.Namespace, "catalog.json", "staged"),
                                InputSchema = ToolDefinitionAcquisitionTarget.InputSchema
                            }
                        }
                    }
                    })
                };
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Path = request.RequestUri!.PathAndQuery;
            Request = await request.Content!.ReadFromJsonAsync<CreateToolDefinitionFromAcquisitionRequest>(cancellationToken);
            Assert.IsNotNull(Request);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new ToolDefinitionResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ToolResourceKinds.ToolDefinition,
                    Metadata = new ResourceMetadata { Name = Request.Name },
                    ScopeRef = Scope,
                    Definition = new FlowToolDefinitionProperties
                    {
                        DisplayName = Request.DisplayName,
                        InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                        Flow = new ToolDefinitionFlowTarget { Name = "acquisition" }
                    }
                })
            };
        }
    }

    private sealed class SourceClientStub(bool targeted = false, bool historyFails = false) : IDataSourcesClient
    {
        public DataSourceAcquisitionResource AcquisitionResult { get; init; } = Acquisition;
        public StartDataSourceAcquisitionRequest? StartedRequest { get; private set; }
        public IReadOnlyList<DataSourceDiscoveryResource> DiscoveryHistory { get; set; } = [Discovery];
        public DataSourceDiscoveryResource AcceptedDiscovery { get; } = Discovery with
        {
            State = DataSourceDiscoveryState.Pending,
            Manifest = null,
            CompletedAt = null,
            LogicalScopeRef = ResourceScopeRef.Instance
        };
        public DataSourceAcquisitionTargetResource TargetResource { get; set; } = Target with { ETag = "\"target-v1\"" };
        public (string Name, ReconcileDataSourceAcquisitionTargetRequest Request, string ETag)? Reconciled { get; private set; }
        public bool ReconcileFails { get; set; }
        public Task<ResourceSnapshot<DataSourceAcquisitionTargetResource>> ReconcileTargetAsync(ResourceNamespace ns, string name,
            ReconcileDataSourceAcquisitionTargetRequest request, string etag, CancellationToken cancellationToken = default)
        {
            Reconciled = (name, request, etag);
            if (ReconcileFails) throw new AgentstrationApiException("Conflict", "test", System.Net.HttpStatusCode.Conflict, "Resource version conflict");
            TargetStale = false;
            TargetResource = TargetResource with { DiscoveryId = request.DiscoveryId, DiscoveryUid = DiscoveryHistory[0].Uid };
            return Task.FromResult(new ResourceSnapshot<DataSourceAcquisitionTargetResource>(TargetResource, "\"target-v2\""));
        }
        public bool DiscoveryReadFails { get; set; }
        public int DiscoveryReads { get; private set; }
        public TaskCompletionSource<IReadOnlyList<DataSourceDiscoveryResource>>? DeferredDiscoveryRead { get; set; }
        public bool ReadingDeferredDiscovery { get; private set; }
        public bool TargetStale { get; set; } = true;
        public Task<IReadOnlyList<DataSourceResource>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceResource>>([Source]);
        public Task<ResourceSnapshot<DataSourceResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => Task.FromResult(new ResourceSnapshot<DataSourceResource>(Source with { Metadata = Source.Metadata with { Name = name } }, "\"etag\""));
        public Task<DataSourceReadiness> GetReadinessAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => Task.FromResult(new DataSourceReadiness(true, true, ResolvedProfile, [],
            new ResolvedDataSourceFlowBinding("data-source-web-acquisition-builtin", ResourceNamespace.Default,
                "1.0.1", false, AcquisitionInputSchema, null, DataSourceFlowContracts.Acquisition,
                 "sha256:flow")
            { ScopeRef = Scope },
            targeted ? new("discover", ResourceNamespace.Default, "1.0.0", false, null, null,
                DataSourceFlowContracts.Discovery, "sha256:discovery")
            { ScopeRef = Scope } : null));
        public Task<IReadOnlyList<DataSourceAcquisitionResource>> GetAcquisitionsAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, CancellationToken cancellationToken = default) => historyFails ? throw new InvalidOperationException("history unavailable") : Task.FromResult<IReadOnlyList<DataSourceAcquisitionResource>>([AcquisitionResult]);
        public Task<ResourceSnapshot<DataSourceResource>> CreateAsync(CreateDataSourceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PutDataSourceRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceAcquisitionResource>> StartAcquisitionAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, StartDataSourceAcquisitionRequest request, CancellationToken cancellationToken = default)
        {
            StartedRequest = request;
            return Task.FromResult(new ResourceSnapshot<DataSourceAcquisitionResource>(Acquisition, "\"acquisition-etag\""));
        }
        public async Task<IReadOnlyList<DataSourceDiscoveryResource>> GetDiscoveriesAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, CancellationToken cancellationToken = default)
        {
            DiscoveryReads++;
            if (DiscoveryReadFails) throw new AgentstrationApiException("unavailable", "test");
            if (DeferredDiscoveryRead is { } deferred)
            {
                DeferredDiscoveryRead = null;
                ReadingDeferredDiscovery = true;
                return await deferred.Task.WaitAsync(cancellationToken);
            }
            return DiscoveryHistory;
        }
        public Task<ResourceSnapshot<DataSourceDiscoveryResource>> StartDiscoveryAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, StartDataSourceDiscoveryRequest request, CancellationToken cancellationToken = default)
        {
            DiscoveryHistory = [AcceptedDiscovery];
            return Task.FromResult(new ResourceSnapshot<DataSourceDiscoveryResource>(AcceptedDiscovery, "\"discovery-etag\""));
        }
        public Task<IReadOnlyList<DataSourceAcquisitionTargetView>> GetAcquisitionTargetsAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceAcquisitionTargetView>>([new(TargetResource, TargetStale, TargetStale ? ["New discovery"] : [])]);
    }

    private static DataSourceDiscoveryResource Discovery => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSourceDiscovery,
        Metadata = new() { Name = "discovery-1" },
        ScopeRef = Scope,
        DataSourceScopeRef = Scope,
        DataSourceUid = Source.Uid,
        DataSourceName = Source.Name,
        DataSourceNamespace = Source.Namespace,
        DataSourceGeneration = Source.Generation,
        Composition = new()
        {
            Profile = ResolvedProfile,
            Flow = new("discover", ResourceNamespace.Default, "1.0.0", false, null, null,
                DataSourceFlowContracts.Discovery, "sha256:discovery")
            { ScopeRef = Scope }
        },
        FlowRunId = "flowrun-discovery",
        State = DataSourceDiscoveryState.Succeeded,
        CorrelationId = "discovery-1",
        RequestHash = "hash",
        CreatedBy = Guid.Empty,
        TenantId = Guid.Empty,
        WorkspaceId = Scope.TargetId!.Value,
        CreatedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        Manifest = new() { Choices = [new() { Id = "documents", DisplayName = "Documents", Selector = JsonSerializer.SerializeToElement(new { collection = "documents" }) }] }
    };
    private static DataSourceAcquisitionTargetResource Target => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSourceAcquisitionTarget,
        Metadata = new() { Name = "documents" },
        ScopeRef = Scope,
        DisplayName = "Documents",
        DataSourceScopeRef = Scope,
        DataSourceUid = Source.Uid,
        DataSourceName = Source.Name,
        DataSourceNamespace = Source.Namespace,
        DataSourceGeneration = Source.Generation,
        DiscoveryId = Discovery.Name,
        DiscoveryUid = Discovery.Uid,
        ChoiceId = "documents",
        ChoiceDisplayName = "Documents",
        Profile = ResolvedProfile,
        PromotedAt = DateTimeOffset.UtcNow,
        PromotedBy = Guid.Empty
    };

    private sealed class ProfileClientStub(bool omitProfile = false) : IDataSourceProfilesClient
    {
        private static readonly DataSourceProfileResource Profile = new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = DataSourceResourceKinds.DataSourceProfile, Metadata = new() { Name = "web-builtin" }, ScopeRef = Scope, Definition = ProfileProperties, ActiveVersion = "1.0.1" };
        public Task<IReadOnlyList<DataSourceProfileResource>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceProfileResource>>(omitProfile ? [] : [Profile]);
        public Task<ResourceSnapshot<DataSourceProfileResource>> GetAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DataSourceProfileRevisionResource>> GetRevisionsAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceProfileRevisionResource>>([new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSourceProfileRevision,
            Metadata = new() { Name = "web-builtin-1-0-1" },
            ScopeRef = Scope,
            ProfileUid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            ProfileName = "web-builtin",
            ProfileGeneration = 1,
            Version = "1.0.1",
            DefinitionHash = "sha256:test",
            DependencyHash = "sha256:dependencies",
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBy = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Definition = ProfileProperties,
            AcquisitionFlow = ResolvedProfile.AcquisitionFlow,
            ToolBindings = []
        }]);
        public Task<ResourceSnapshot<DataSourceProfileResource>> CreateAsync(CreateDataSourceProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileResource>> UpdateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PutDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileRevisionResource>> PublishAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, PublishDataSourceProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<DataSourceProfileResource>> ActivateAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, ActivateDataSourceProfileRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
