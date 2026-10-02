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
    public void StagedArtifactContentIsOnlyReadAfterExplicitAction()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        var client = new ArtifactClient();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IArtifactsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.StagedArtifactDetails>(parameters =>
            parameters.Add(value => value.Id, client.Artifact.ArtifactId.ToString()));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(0, client.ContentReadCount);
            StringAssert.Contains(rendered.Markup, "Read content");
        });

        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Read content").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, client.ContentReadCount);
            StringAssert.Contains(rendered.Markup, "governed preview");
        });
    }

    private sealed class ArtifactClient : IArtifactsClient
    {
        public ArtifactClient()
        {
            var now = DateTimeOffset.UtcNow;
            Artifact = new StagedArtifactView(
                StagedArtifactId.New(), null, "notes.txt", "text/plain", 16, "sha256",
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
            return Task.FromResult(new ArtifactContentChunk(0, Convert.ToBase64String(Encoding.UTF8.GetBytes("governed preview")), true));
        }

        public Task<StagedArtifactView> ExtendRetentionAsync(StagedArtifactId id, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PurgeAsync(StagedArtifactId id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FlowRunArtifactResource>> GetDurableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FlowRunArtifactResource>>([]);
    }
}
