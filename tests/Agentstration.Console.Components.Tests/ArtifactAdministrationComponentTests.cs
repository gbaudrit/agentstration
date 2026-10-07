using System.Text;
using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ArtifactAdministrationComponentTests
{
    [TestMethod]
    public void StagedArtifactContentIsOnlyReadAfterExplicitActionAndUsesTabs()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        var client = new ArtifactClient("governed preview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(0, client.ContentReadCount);
            Assert.AreEqual(3, rendered.FindAll("[role='tab']").Count);
        });

        rendered.Find("[data-testid='staged-artifact-content-tab']").Click();
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
        using var context = new BunitContext();
        const string expected = "complete governed artifact content";
        var client = new ArtifactClient(expected, maximumBackendChunk: 8);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForAssertion(() =>
            Assert.AreEqual(0, client.ContentReadCount));

        rendered.Find("[data-testid='staged-artifact-content-tab']").Click();
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
        using var context = new BunitContext();
        var client = new ArtifactClient("preview", declaredLength: 4L * 1024 * 1024 + 1);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForElement("[data-testid='staged-artifact-overview-tab']");
        rendered.Find("[data-testid='staged-artifact-content-tab']").Click();

        var fullContentButton = rendered.Find("[data-testid='staged-artifact-read-full']");
        Assert.IsTrue(fullContentButton.HasAttribute("disabled"));
        Assert.IsNotNull(rendered.Find("[data-testid='staged-artifact-full-content-limit']"));
        Assert.AreEqual(0, client.ContentReadCount);
    }

    [TestMethod]
    public void StagedArtifactMovesDeleteToHeaderAndRequiresALaterRetentionDate()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        var client = new ArtifactClient("preview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForElement("[data-testid='staged-artifact-delete']");
        Assert.AreEqual("Delete", rendered.Find("[data-testid='staged-artifact-delete']").TextContent.Trim());

        rendered.Find("[data-testid='staged-artifact-retention-tab']").Click();
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
        using var context = new BunitContext();
        var client = new ArtifactClient("preview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.Artifacts>();
        rendered.WaitForElement("[data-testid='durable-artifact-row']");
        Assert.IsNotNull(rendered.Find($"a[href='/artifacts/durable/{client.Durable.ArtifactId}']"));
        StringAssert.Contains(rendered.Markup, "index.html");
        StringAssert.Contains(
            rendered.Find($"a[href='/artifacts/durable/{client.Durable.ArtifactId}']").TextContent,
            client.Durable.ArtifactId.ToString());

        rendered.FindAll("button").Single(value => value.TextContent.Contains("Local copies", StringComparison.Ordinal)).Click();
        Assert.IsNotNull(rendered.Find($"a[href='/artifacts/staged/{client.Artifact.ArtifactId}']"));
        Assert.IsNotNull(rendered.Find("a[href='/flow-runs/flow-run-1']"));
        StringAssert.Contains(rendered.Markup, "index.html");
        StringAssert.Contains(
            rendered.Find($"a[href='/artifacts/staged/{client.Artifact.ArtifactId}']").TextContent,
            client.Artifact.ArtifactId.ToString());
    }

    [TestMethod]
    public void ArtifactTerminologyExplainsAvailabilityInFrench()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        var client = new ArtifactClient("aperçu");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var list = context.Render<Agentstration.Web.Components.Pages.Artifacts>();
        list.WaitForElement("[data-testid='durable-artifact-row']");
        StringAssert.Contains(list.Markup, "Liste");
        StringAssert.Contains(list.Markup, "Copies locales");

        var details = context.Render<Agentstration.Web.Components.Pages.FlowRunArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));
        details.WaitForElement("[data-testid='durable-artifact-content-tab']");
        details.Find("[data-testid='durable-artifact-content-tab']").Click();
        StringAssert.Contains(details.Markup, "copie locale");
        Assert.IsFalse(details.Markup.Contains("matérialis", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void StagedArtifactDisplaysMegabytesAndKeepsTheExactByteCount()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
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
        using var context = new BunitContext();
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
        });
    }

    [TestMethod]
    public void DurableArtifactHasGovernedMetadataDetails()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        var client = new ArtifactClient("preview");
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.FlowRunArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Durable.ArtifactId.ToString()));

        rendered.WaitForElement("[data-testid='durable-artifact-overview-tab']");
        StringAssert.Contains(rendered.Markup, "index.html");
        Assert.IsNotNull(rendered.Find($"a[href='/artifacts/staged/{client.Artifact.ArtifactId}']"));

        rendered.Find("[data-testid='durable-artifact-content-tab']").Click();
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

        rendered.Find("[data-testid='durable-artifact-storage-tab']").Click();
        StringAssert.Contains(rendered.Markup, "Immutable storage receipt");
        Assert.IsFalse(rendered.Markup.Contains("opaque-reference", StringComparison.Ordinal));
    }

    private sealed class ArtifactClient : IArtifactsClient
    {
        private readonly byte[] content;
        private readonly int maximumBackendChunk;

        public ArtifactClient(string content, int maximumBackendChunk = int.MaxValue, long? declaredLength = null)
        {
            this.content = Encoding.UTF8.GetBytes(content);
            this.maximumBackendChunk = maximumBackendChunk;
            var now = DateTimeOffset.UtcNow;
            Artifact = new StagedArtifactView(
                StagedArtifactId.New(), null, "index.html", "text/plain", declaredLength ?? this.content.Length, "sha256",
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

        public StagedArtifactView Artifact { get; }
        public FlowRunArtifactResource Durable { get; }
        public int ContentReadCount { get; private set; }

        public Task<IReadOnlyList<StagedArtifactView>> GetStagedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StagedArtifactView>>([Artifact]);

        public Task<StagedArtifactView?> GetStagedAsync(StagedArtifactId id, CancellationToken cancellationToken = default) =>
            Task.FromResult<StagedArtifactView?>(Artifact);

        public Task<ArtifactContentChunk> ReadContentAsync(StagedArtifactId id, long offset, int length, CancellationToken cancellationToken = default)
        {
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

        public Task<StagedArtifactView> ExtendRetentionAsync(StagedArtifactId id, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PurgeAsync(StagedArtifactId id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FlowRunArtifactResource>> GetDurableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FlowRunArtifactResource>>([Durable]);

        public Task<FlowRunArtifactResource?> GetDurableAsync(FlowRunArtifactId id, CancellationToken cancellationToken = default) =>
            Task.FromResult<FlowRunArtifactResource?>(id == Durable.ArtifactId ? Durable : null);

        public Task<FlowRunArtifactMaterialization> StartMaterializationAsync(FlowRunArtifactId id,
            CancellationToken cancellationToken = default) => Task.FromResult(new FlowRunArtifactMaterialization(
                "flowrun-materialize-1", "Succeeded", Artifact.ArtifactId, StagedArtifactAvailable: true,
                StagedArtifactExpiresAt: Artifact.ExpiresAt));

        public Task<IReadOnlyList<FlowRunArtifactMaterialization>> GetMaterializationsAsync(FlowRunArtifactId id,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FlowRunArtifactMaterialization>>([
                new("flowrun-materialize-1", "Succeeded", Artifact.ArtifactId, StagedArtifactAvailable: true,
                    StagedArtifactExpiresAt: Artifact.ExpiresAt)
            ]);

        public Task<FlowRunArtifactMaterialization?> GetMaterializationAsync(FlowRunArtifactId id, string flowRunId,
            CancellationToken cancellationToken = default) => Task.FromResult<FlowRunArtifactMaterialization?>(new(
                flowRunId, "Succeeded", Artifact.ArtifactId, StagedArtifactAvailable: true,
                StagedArtifactExpiresAt: Artifact.ExpiresAt));
    }
}
