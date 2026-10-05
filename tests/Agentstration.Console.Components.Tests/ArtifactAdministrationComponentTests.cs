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
                StagedArtifactId.New(), null, "notes.txt", "text/plain", declaredLength ?? this.content.Length, "sha256",
                StagedArtifactStatus.Sealed,
                new ArtifactProducer { Kind = ArtifactProducerKind.FlowRun, Id = "flow-run-1", FlowRunId = "flow-run-1" },
                "default", ResourceNamespace.Default, "filesystem", ResourceNamespace.Default, "1.0.0",
                now, now, now.AddHours(1), null, null, null, null, []);
        }

        public StagedArtifactView Artifact { get; }
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

        public Task<StagedArtifactView> ExtendRetentionAsync(StagedArtifactId id, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PurgeAsync(StagedArtifactId id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FlowRunArtifactResource>> GetDurableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FlowRunArtifactResource>>([]);
    }
}
