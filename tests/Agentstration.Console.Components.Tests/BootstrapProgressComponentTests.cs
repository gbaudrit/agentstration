using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Bootstrap;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BootstrapProgressComponentTests
{
    [TestMethod]
    [DataRow("en-US", "View YAML files", "Profile file")]
    [DataRow("fr-FR", "Voir les fichiers YAML", "Fichier du profil")]
    public void OriginalProfileYamlIsLoadedOnDemandAndRemainsReadOnly(string cultureName, string button, string fileLabel)
    {
        using var culture = new TestCultureScope(cultureName);
        var api = new StubClient();
        using var context = Context(api, application: false);
        var cut = context.Render<BootstrapProfileSourceViewer>(p => p.Add(x => x.Profile, "nantes-alertes"));
        Assert.AreEqual(0, api.FileReads);
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("kind: BootstrapProfile", cut.Find("textarea").GetAttribute("value")));
        Assert.IsTrue(cut.Find("textarea").HasAttribute("readonly"));
        StringAssert.Contains(cut.Markup, button);
        StringAssert.Contains(cut.Markup, fileLabel);
        Assert.AreEqual(2, api.FileReads);
        cut.Render(p => p.Add(x => x.Profile, "another-profile"));
        Assert.AreEqual(0, cut.FindAll("textarea").Count);
    }

    [TestMethod]
    [DataRow("en-US", "Required version", "Required by resource")]
    [DataRow("fr-FR", "Version attendue", "Requise par la ressource")]
    public void MissingContributionShowsItsExactSelectorAndConsumer(string cultureName, string versionLabel, string consumerLabel)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = Context(new StubClient(), application: false);
        var cut = context.Render<BootstrapDependencyDetails>(p => p
            .Add(x => x.Dependency, new("agentstration.extensions.postgresql", "StorageType", "postgresql.jsonb", "1.0.0", null, "Storage", "nantes-alertes-jsonb"))
            .Add(x => x.Reason, "contribution_unavailable"));
        foreach (var value in new[] { versionLabel, consumerLabel, "StorageType", "postgresql.jsonb", "1.0.0", "Storage/nantes-alertes-jsonb" })
            StringAssert.Contains(cut.Find("[data-testid='bootstrap-dependency-details']").TextContent, value);
    }

    [TestMethod]
    public void SourceReadFailuresExposeNoTransportDetailsAndCanBeRetried()
    {
        var api = new StubClient { FailFiles = true };
        using var context = Context(api, application: false);
        var cut = context.Render<BootstrapProfileSourceViewer>(p => p.Add(x => x.Profile, "nantes-alertes"));
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, cut.FindAll("[role='alert']").Count));
        Assert.DoesNotContain("private-transport-sentinel", cut.Markup, StringComparison.Ordinal);
        Assert.AreEqual(0, cut.FindAll("textarea").Count);
        api.FailFiles = false;
        cut.Find(".button-secondary").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("kind: BootstrapProfile", cut.Find("textarea").GetAttribute("value")));
        Assert.AreEqual(0, cut.FindAll("[role='alert']").Count);
    }

    [TestMethod]
    [DataRow("en-US", "Waiting for a Worker", "View discovery Flow Run")]
    [DataRow("fr-FR", "En attente d’un Worker", "Voir l’exécution de la découverte")]
    public void DiscoveryWaitUsesLocalizedDurableStageAndLogicalRunContext(string cultureName, string stage, string link)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = Context(new StubClient());
        var application = new BootstrapApplicationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Metadata = new() { Name = "discovery" },
            Definition = new()
            {
                Profiles = ["nantes-alertes"],
                CurrentResource = new("nantes-alertes", "60.yaml", "ConnectedServiceOperationDiscovery", "nantes", BootstrapResourceDisposition.Create),
                Operation = new("waiting_worker", DateTimeOffset.UnixEpoch, "flowrun-discovery")
            }
        };
        var cut = context.Render<BootstrapApplicationProgress>(p => p.Add(x => x.Application, application));
        StringAssert.Contains(cut.Find("[data-testid='bootstrap-operation-stage']").TextContent, stage);
        var anchor = cut.Find("[data-testid='bootstrap-operation-stage'] a");
        Assert.AreEqual(link, anchor.TextContent);
        Assert.AreEqual("/flow-runs/flowrun-discovery?context=%2Finstance", anchor.GetAttribute("href"));
        Assert.HasCount(0, cut.FindAll("[data-testid='bootstrap-operation-stage'] progress"));
    }

    [TestMethod]
    public async Task FollowingTracksTheActiveResourcePageAndTerminalFailureWithoutStealingManualInspection()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context(new StubClient());
        var plan = Enumerable.Range(1, 60).Select(index => new BootstrapPlannedResource("batch", index + ".yaml", "Parameter", "parameter-" + index)).ToArray();
        var application = new BootstrapApplicationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Metadata = new() { Name = "follow" },
            Definition = new()
            {
                Profiles = ["batch"],
                TotalResources = 60,
                ResourcePlan = plan,
                CurrentResource = new("batch", "41.yaml", "Parameter", "parameter-41", BootstrapResourceDisposition.Create)
            }
        };
        var cut = context.Render<BootstrapApplicationProgress>(p => p.Add(x => x.Application, application));
        StringAssert.Contains(cut.Find("[data-progress-current='true']").TextContent, "parameter-41");
        Assert.HasCount(25, cut.FindAll("[data-testid='bootstrap-application-resource']"));
        application = application with
        {
            Definition = application.Definition with
            {
                CurrentResource = null,
                Resources = plan.Take(41).Select(row => new BootstrapAppliedResource(row.Profile, row.Location, row.Kind, row.Name, BootstrapResourceDisposition.Create)).ToArray()
            }
        };
        cut.Render(p => p.Add(x => x.Application, application));
        StringAssert.Contains(cut.Find("[data-progress-current='true']").TextContent, "parameter-41");
        await cut.InvokeAsync(cut.FindComponent<StepProgress>().Instance.PauseFollowingAsync);
        cut.Find("input[type='search']").Input("parameter-26");
        application = application with { Definition = application.Definition with { CurrentResource = new("batch", "52.yaml", "Parameter", "parameter-52", BootstrapResourceDisposition.Create) } };
        cut.Render(p => p.Add(x => x.Application, application));
        StringAssert.Contains(cut.Markup, "parameter-26");
        cut.Find(".step-progress-follow").Click();
        StringAssert.Contains(cut.Find("[data-progress-current='true']").TextContent, "parameter-52");
        var failure = new BootstrapFailure("validation", "data_source_profile_not_published", "test-log-reference");
        application = application with
        {
            Definition = application.Definition with
            {
                Status = BootstrapApplicationStatus.PartiallyApplied,
                CurrentResource = null,
                Failure = failure,
                ResourcePlan = [.. plan, new("next", "failure.yaml", "DataSourceProfile", "failed-profile")],
                Resources = [.. plan.Select(row => new BootstrapAppliedResource(row.Profile, row.Location, row.Kind, row.Name, BootstrapResourceDisposition.Create)),
                new("next", "failure.yaml", "DataSourceProfile", "failed-profile", BootstrapResourceDisposition.Failed, "PRIVATE-ERROR-BODY") { Failure = failure }]
            }
        };
        cut.Render(p => p.Add(x => x.Application, application));
        StringAssert.Contains(cut.Find("[role='tabpanel']").TextContent, "data_source_profile_not_published");
        StringAssert.Contains(cut.Markup, "test-log-reference");
        Assert.IsFalse(cut.Markup.Contains("PRIVATE-ERROR-BODY", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("en-US", "Processed 1 of 512 resources", "Created", "Resource 2:")]
    [DataRow("fr-FR", "1 ressources traitées sur 512", "Créées", "Ressource 2 :")]
    public void ReloadResumesPersistedProgressWithoutSubmittingAgain(string locale, string progress, string created, string current)
    {
        using var culture = new TestCultureScope(locale);
        var client = new StubClient();
        using var context = Context(client);
        var rendered = context.Render<BootstrapProfiles>();
        rendered.WaitForAssertion(() =>
        {
            var panel = rendered.Find("[data-testid='bootstrap-application-progress']");
            StringAssert.Contains(panel.TextContent, progress);
            Assert.AreEqual("1", panel.QuerySelector(".metric-card strong")?.TextContent.Trim());
            StringAssert.Contains(panel.QuerySelector(".metric-card")!.TextContent, created);
            Assert.IsTrue(panel.QuerySelector(".step-progress") is not null);
            StringAssert.Contains(panel.TextContent, current);
            Assert.AreEqual("512", panel.QuerySelector("progress")?.GetAttribute("max"));
            Assert.AreEqual("1", panel.QuerySelector("progress")?.GetAttribute("value"));
            StringAssert.Contains(panel.TextContent, "Parameter/bootstrap-load-test-002");
        });
        Assert.AreEqual(0, client.Submissions);
    }

    [TestMethod]
    public void PinnedIdentitySurvivesANewerApplicationInHistory()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { IncludeNewerApplication = true };
        using var context = Context(client);
        context.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/settings/bootstrap?applicationId=retained-application");
        var rendered = context.Render<BootstrapProfiles>();
        rendered.WaitForAssertion(() => Assert.AreEqual("retained-application",
            rendered.Find("#bootstrap-application-detail").GetAttribute("data-application-id")));
        Assert.AreEqual(0, client.Submissions);
    }

    [TestMethod]
    public void SelectingHistoryPinsItsIdentityForReload()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Context(new StubClient(), application: false);
        var rendered = context.Render<BootstrapProfiles>();
        rendered.FindAll("button").Single(button => button.TextContent == "View").Click();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.AreEqual("?applicationId=retained-application", new Uri(navigation.Uri).Query);
    }

    [TestMethod]
    public void ObservationRetriesSameApplicationAndReachesTerminalState()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { DisconnectOnce = true };
        using var context = Context(client);
        var rendered = context.Render<BootstrapProfiles>();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup,
            "Progress updates are temporarily unavailable"), TimeSpan.FromSeconds(5));
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid='bootstrap-application-progress']").TextContent, "Succeeded");
            Assert.AreEqual("2", rendered.Find("progress").GetAttribute("value"));
        }, TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, client.Submissions);
        Assert.IsTrue(client.Observations >= 2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancelRequestsCancellationForExistingIdentity(bool changeSelection)
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { ExposeCatalog = changeSelection };
        using var context = Context(client);
        var rendered = context.Render<BootstrapProfiles>();
        if (changeSelection)
        {
            rendered.FindAll("button").Single(button => button.TextContent == "Back to preparation").Click();
            rendered.Find("[data-profile-name='base']").Change(true);
            rendered.FindAll("button").Single(button => button.TextContent == "View").Click();
        }
        rendered.FindAll("button").Single(button => button.TextContent == "Cancel application").Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "Cancellation requested."));
        Assert.AreEqual("retained-application", client.CancelledId);
        Assert.AreEqual(0, client.Submissions);
    }

    [TestMethod]
    public void CatalogUsesTheSharedListAndPreviewStillPrecedesConfirmedSubmission()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { ExposeCatalog = true };
        using var context = Context(client, application: false);
        var cut = context.Render<BootstrapProfiles>();
        Assert.AreEqual(1, cut.FindComponents<ResourceCollectionTemplate<BootstrapProfileSummary>>().Count);
        cut.Find("[data-profile-name='base']").Change(true);
        Assert.AreEqual(0, client.Submissions);
        cut.FindAll("button").Single(button => button.TextContent == "Preview application").Click();
        Assert.AreEqual(1, client.Previews);
        Assert.AreEqual(0, client.Submissions);
        StringAssert.Contains(cut.Find(".bootstrap-preview-summary").TextContent, "Ready");
        cut.FindAll("button").Single(button => button.TextContent == "Apply preview").Click();
        Assert.AreEqual(0, client.Submissions);
        cut.Find("[role='dialog']").QuerySelectorAll("button").Single(button => button.TextContent == "Apply profiles").Click();
        Assert.AreEqual(1, client.Submissions);
        Assert.AreEqual(1, cut.FindAll("[data-testid='bootstrap-manual-step-progress']").Count);
    }

    [TestMethod]
    public void InvalidPreviewIsAPreparationFailureWithoutPretendingResourcesWereApplied()
    {
        using var context = Context(new StubClient());
        var application = new BootstrapApplicationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = BootstrapKinds.BootstrapApplication,
            Metadata = new() { Name = "invalid" },
            Definition = new()
            {
                Status = BootstrapApplicationStatus.Failed,
                Error = "bootstrap_application_invalid",
                TotalResources = 1,
                ResourcePlan = [new("base", "one.yaml", "Parameter", "one")]
            }
        };
        var cut = context.Render<BootstrapApplicationProgress>(p => p.Add(x => x.Application, application));
        Assert.IsTrue(cut.Find("[data-step-id='prepare']").ClassList.Contains("status-failed"));
        Assert.IsTrue(cut.Find("[data-step-id='resources']").ClassList.Contains("status-interrupted"));
        Assert.AreEqual("true", cut.Find("[data-step-id='prepare']").GetAttribute("aria-selected"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MixedScopesHaveIndividualTargetsAndPreviewIsASeparateScreenWithPreservedPreparation(bool plannedTargets)
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient { MixedCatalog = true };
        using var context = Context(client, application: false);
        var cut = context.Render<BootstrapProfiles>();
        foreach (var name in new[] { "workspace", "tenant", "base" }) cut.Find($"[data-profile-name='{name}']").Change(true);
        CollectionAssert.AreEqual(new[] { "base", "tenant", "workspace" }, cut.FindAll("[data-bootstrap-profile]").Select(node => node.GetAttribute("data-bootstrap-profile")).ToArray());
        var tenantSelect = cut.Find("[data-target-profile='tenant']");
        tenantSelect.Change(plannedTargets ? tenantSelect.QuerySelectorAll("option").Single(option => option.TextContent.Contains("future-tenant", StringComparison.Ordinal)).GetAttribute("value") : StubClient.TenantId.ToString());
        var workspaceSelect = cut.Find("[data-target-profile='workspace']");
        workspaceSelect.Change(plannedTargets ? workspaceSelect.QuerySelectorAll("option").Single(option => option.TextContent.Contains("future-workspace", StringComparison.Ordinal)).GetAttribute("value") : StubClient.WorkspaceId.ToString());
        cut.FindAll("button").Single(button => button.TextContent == "Preview application").Click();
        var selection = client.LastSelection;
        Assert.IsNotNull(selection);
        Assert.IsNotNull(selection.Applications);
        CollectionAssert.AreEqual(new[] { "base", "tenant", "workspace" }, selection.Applications.Select(item => item.Profile).ToArray());
        Assert.AreEqual(0, cut.FindAll("[data-testid='distribution-bootstrap-profiles']").Count);
        Assert.AreEqual(1, cut.FindAll(".bootstrap-preview-summary").Count);
        Assert.AreEqual(0, client.Submissions);
        cut.FindAll("button").Single(button => button.TextContent == "Back to preparation").Click();
        Assert.AreEqual(1, cut.FindAll("[data-testid='distribution-bootstrap-profiles']").Count);
        Assert.AreEqual(0, cut.FindAll(".bootstrap-preview-summary").Count);
        Assert.IsFalse(string.IsNullOrEmpty(cut.Find("[data-target-profile='workspace']").GetAttribute("value")));
        Assert.HasCount(3, cut.FindAll("input[data-profile-name]:checked"));
    }

    [TestMethod]
    [DataRow("en-US", "Installed prerequisites", "Missing or unavailable", "Synchronized")]
    [DataRow("fr-FR", "Prérequis installés", "Absent ou indisponible", "Synchronisé")]
    public void SeedReviewLocalizesPrerequisitesAndRetainsExactProjectAndRevisionLinks(string cultureName, string title, string missing, string synchronized)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = Context(new StubClient(), application: false);
        var target = new BootstrapApplicationTarget(StubClient.TenantId, StubClient.WorkspaceId);
        var provenance = new WorkspaceSeedProvenance(Guid.NewGuid(), target, Guid.NewGuid(), "1.0.0", "release-one", new string('a', 64), "seed", "bootstrap", null, null, null, DateTimeOffset.UnixEpoch)
        { ImportPlans = [new(Guid.NewGuid(), new string('b', 64))] };
        var project = Guid.NewGuid();
        var handover = new WorkspaceSeedHandover(provenance.Id, WorkspaceSeedHandoverStatus.Succeeded, project, "fixture", default, "revision-1", [], DateTimeOffset.UnixEpoch)
        { RequirementsPublicationId = Guid.NewGuid(), RequirementsDigest = new string('c', 64) };
        var cut = context.Render<WorkspaceSeedReview>(p => p.Add(x => x.Provenance, provenance).Add(x => x.Handover, handover)
            .Add(x => x.Prerequisites, [new("Worker", "coding-cli", false, "seed_native_worker_missing")]));
        Assert.Contains(title, cut.Markup); Assert.Contains(missing, cut.Markup); Assert.Contains(synchronized, cut.Markup);
        Assert.Contains("seed_native_worker_missing", cut.Markup); Assert.Contains("revision-1", cut.Markup);
        Assert.Contains(provenance.ImportPlans[0].PlanId.ToString(), cut.Markup);
        Assert.Contains(provenance.ImportPlans[0].ExpectedDigest, cut.Markup);
        var labels = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<BootstrapProfilesStrings>>();
        Assert.AreEqual(cultureName == "fr-FR" ? "Invalide" : "Invalid", labels["Disposition.Invalid"].Value);
        Assert.AreEqual($"/api/workspaces/{target.WorkspaceId:D}/projects/{project:D}", cut.FindAll("a")[0].GetAttribute("href"));
        Assert.AreEqual($"/api/workspaces/{target.WorkspaceId:D}/projects/{project:D}/sharing/operations/{handover.RequirementsPublicationId:D}", cut.FindAll("a")[1].GetAttribute("href"));
        Assert.Contains(handover.RequirementsDigest, cut.Markup);
        Assert.Contains(cultureName == "fr-FR" ? "Déclarations de connexion partagées vérifiées" : "Shared connection declarations verified", cut.Markup);
        Assert.Contains(cultureName == "fr-FR" ? "Publication des déclarations de connexion" : "Connection declaration publication", cut.Markup);
    }

    [TestMethod]
    public void SeedSourceViewerCarriesTheExactTargetAndPinAndDropsPreviousFilesWhenThePinChanges()
    {
        var api = new StubClient(); using var context = Context(api, application: false);
        var pin = new WorkspaceSeedSelection(Guid.NewGuid(), new string('a', 64));
        var target = new BootstrapApplicationTarget(StubClient.TenantId, StubClient.WorkspaceId);
        var cut = context.Render<BootstrapProfileSourceViewer>(p => p.Add(x => x.Profile, "seed").Add(x => x.Seed, pin).Add(x => x.Target, target));
        cut.Find("button").Click(); cut.WaitForAssertion(() => Assert.IsNotNull(api.LastFileRequest?.File));
        Assert.AreEqual(pin, api.LastFileRequest!.Seed); Assert.AreEqual(target, api.LastFileRequest.Target);
        cut.Render(p => p.Add(x => x.Seed, pin with { Id = Guid.NewGuid() }));
        Assert.IsFalse(cut.FindAll("textarea").Any());
        Assert.AreEqual("false", cut.Find("button").GetAttribute("aria-expanded"));
    }

    private static BunitContext Context(StubClient client, bool application = true)
    {
        var context = new BunitContext();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/collection-ui.js").Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/step-progress.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IBootstrapProfilesApiClient>(client);
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        if (application) context.Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/bootstrap?applicationId=retained-application");
        return context;
    }

    private sealed class StubClient : IBootstrapProfilesApiClient
    {
        public Task<WorkspaceSeedReceipt> IngestSeedAsync(IngestWorkspaceSeedRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkspaceSeedReceipt> GetSeedAsync(Guid id, BootstrapApplicationTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();
        public BootstrapProfileFilesRequest? LastFileRequest { get; private set; }
        public int FileReads { get; private set; }
        public bool FailFiles { get; set; }
        public Task<BootstrapProfileFiles> GetFilesAsync(BootstrapProfileFilesRequest request, CancellationToken cancellationToken)
        {
            FileReads++; LastFileRequest = request;
            if (FailFiles) throw new HttpRequestException("private-transport-sentinel");
            return Task.FromResult(new BootstrapProfileFiles(request.Profile, "digest", ["profile.yaml"], request.File,
                request.File is null ? null : "kind: BootstrapProfile"));
        }
        private readonly BootstrapApplicationResource application = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = BootstrapKinds.BootstrapApplication,
            Metadata = new() { Name = "retained-application" },
            Definition = new()
            {
                Profiles = ["load-test"],
                Status = BootstrapApplicationStatus.Running,
                StartedAt = DateTimeOffset.UnixEpoch,
                TotalResources = 512,
                ResourcePlan = Enumerable.Range(1, 512).Select(index => new BootstrapPlannedResource("load-test", $"10-parameters.yaml#{index}", "Parameter", $"bootstrap-load-test-{index:D3}")).ToArray(),
                Resources = [new("load-test", "10-parameters.yaml#1", "Parameter", "bootstrap-load-test-001", BootstrapResourceDisposition.Create)],
                CurrentResource = new("load-test", "10-parameters.yaml#2", "Parameter", "bootstrap-load-test-002", BootstrapResourceDisposition.Create)
            }
        };
        public bool ExposeCatalog { get; init; }
        public bool MixedCatalog { get; init; }
        public BootstrapProfileSelection? LastSelection { get; private set; }
        public static Guid TenantId { get; } = Guid.NewGuid();
        public static Guid WorkspaceId { get; } = Guid.NewGuid();
        private static IReadOnlyList<BootstrapProfileSummary> MixedProfiles =>
        [
            CatalogProfile with { Topology = [new("Tenant", "future-tenant")] },
            new("tenant", "Tenant fixture", null, BootstrapProfileScope.Tenant, 1, 1, "tenant-digest", []) { Topology = [new("Workspace", "future-workspace")] },
            new("workspace", "Workspace fixture", null, BootstrapProfileScope.Workspace, 1, 1, "workspace-digest", [])
        ];
        public int Previews { get; private set; }
        private BootstrapApplicationResource? submitted;
        private static BootstrapProfileSummary CatalogProfile => new("base", "Base", "Fixture profile", BootstrapProfileScope.Instance, 1, 1, new string('a', 64), []);
        public bool DisconnectOnce { get; init; }
        public bool IncludeNewerApplication { get; init; }
        private BootstrapApplicationResource Newer => application with
        {
            Metadata = application.Metadata with { Name = "newer-application" },
            Definition = application.Definition with { StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(1) }
        };
        public int Submissions { get; private set; }
        public int Observations { get; private set; }
        public string? CancelledId { get; private set; }
        public Task<BootstrapManagementView> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new BootstrapManagementView(new(null, false, [], MixedCatalog ? MixedProfiles : ExposeCatalog ? [CatalogProfile] : []), MixedCatalog ? [new(TenantId, "current", "Current Tenant")] : [], MixedCatalog ? [new(TenantId, "current", "Current Tenant", WorkspaceId, "current", "Current Workspace")] : [], IncludeNewerApplication ? [Newer, application] : [application]));
        public Task<BootstrapApplicationResource?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken)
        {
            if (submitted?.Metadata.Name == applicationId) return Task.FromResult<BootstrapApplicationResource?>(submitted);
            if (applicationId == "newer-application") return Task.FromResult<BootstrapApplicationResource?>(Newer);
            Assert.AreEqual(application.Metadata.Name, applicationId);
            Observations++;
            if (DisconnectOnce && Observations == 2) throw new HttpRequestException("Synthetic disconnect");
            if (!DisconnectOnce || Observations == 1) return Task.FromResult<BootstrapApplicationResource?>(application);
            return Task.FromResult<BootstrapApplicationResource?>(application with
            {
                Definition = application.Definition with
                {
                    Status = BootstrapApplicationStatus.Succeeded,
                    CurrentResource = null,
                    Resources = [.. application.Definition.Resources,
                        new("load-test", "10-parameters.yaml#2", "Parameter", "bootstrap-load-test-002", BootstrapResourceDisposition.Skip)]
                }
            });
        }
        public Task<BootstrapApplicationResource> CancelAsync(string applicationId, CancellationToken cancellationToken)
        {
            CancelledId = applicationId;
            return Task.FromResult(application with { Definition = application.Definition with { CancellationRequested = true } });
        }
        public Task<BootstrapApplicationResource> ApplyAsync(BootstrapProfileSelection selection, string expectedDigest, CancellationToken cancellationToken, Guid? requestId = null)
        {
            Submissions++;
            submitted = application with { Metadata = application.Metadata with { Name = "submitted" }, Definition = new() { Profiles = selection.Profiles, Status = BootstrapApplicationStatus.Running } };
            return Task.FromResult(submitted);
        }
        public Task<BootstrapProfileSummary> GetSourceProfileAsync(BootstrapSourceProfileSelection selection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<BootstrapBindingTargetOption>> GetBindingTargetsAsync(BootstrapBindingTargetsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BootstrapCompositionPreview> PreviewAsync(BootstrapProfileSelection selection, CancellationToken cancellationToken)
        {
            Previews++;
            LastSelection = selection;
            if (selection.Applications is { Count: > 0 } applications)
                return Task.FromResult(new BootstrapCompositionPreview(applications.Select(item => MixedProfiles.Single(profile => profile.Name == item.Profile)).ToArray(), BootstrapProfileScope.Instance, null, [], new string('a', 64),
                    applications.Select(item => new BootstrapResourcePreview(item.Profile, item.Profile + ".yaml", "Parameter", item.Profile, BootstrapResourceDisposition.Create)).ToArray())
                { Applications = applications.Select(item => new BootstrapProfileApplicationPreview(item.Profile, MixedProfiles.Single(profile => profile.Name == item.Profile).Scope, item.PlannedTarget?.TenantName ?? item.Target?.TenantId?.ToString() ?? "/instance", item.PlannedTarget is not null)).ToArray() });
            return Task.FromResult(new BootstrapCompositionPreview([CatalogProfile], BootstrapProfileScope.Instance, null, [], new string('a', 64),
                [new("base", "one.yaml", "Parameter", "one", BootstrapResourceDisposition.Create)]));
        }
    }
}
