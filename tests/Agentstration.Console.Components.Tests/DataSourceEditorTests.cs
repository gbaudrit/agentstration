using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
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
            Assert.HasCount(1, rendered.FindAll("[data-testid=data-source-discovery-choice]"));
            Assert.IsFalse(rendered.Find("[data-testid=data-source-start-discovery]").HasAttribute("disabled"));
            Assert.DoesNotContain(cultureName == "fr-FR" ? "Obsolète" : "Stale", rendered.Markup);
        });
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

    private sealed class SourceClientStub(bool targeted = false, bool historyFails = false) : IDataSourcesClient
    {
        public StartDataSourceAcquisitionRequest? StartedRequest { get; private set; }
        public IReadOnlyList<DataSourceDiscoveryResource> DiscoveryHistory { get; set; } = [Discovery];
        public DataSourceDiscoveryResource AcceptedDiscovery { get; } = Discovery with
        {
            State = DataSourceDiscoveryState.Pending, Manifest = null, CompletedAt = null,
            LogicalScopeRef = ResourceScopeRef.Instance
        };
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
        public Task<IReadOnlyList<DataSourceAcquisitionResource>> GetAcquisitionsAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, CancellationToken cancellationToken = default) => historyFails ? throw new InvalidOperationException("history unavailable") : Task.FromResult<IReadOnlyList<DataSourceAcquisitionResource>>([Acquisition]);
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
        public Task<IReadOnlyList<DataSourceAcquisitionTargetView>> GetAcquisitionTargetsAsync(ResourceNamespace ns, string name, ResourceScopeRef sourceScope, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DataSourceAcquisitionTargetView>>([new(Target, TargetStale, TargetStale ? ["New discovery"] : [])]);
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
