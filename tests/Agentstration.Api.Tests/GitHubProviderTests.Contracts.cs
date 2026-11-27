extern alias GitHubExtension;
using System.Text.Json;
using Agentstration.Aep.Client;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Aep;
using Agentstration.Afc.Client;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Aep;
using Agentstration.Atc.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using GitHub = GitHubExtension::Agentstration.Extensions.GitHub;

namespace Agentstration.Web.Tests;

public sealed partial class GitHubProviderTests
{
    [TestMethod]
    public async Task TypedGitHubContractsAdvertiseGovernedServiceAndRecoverOriginalIssueAndPrAfterRestart()
    {
        using var fixture = new Fixture();
        var envelope = ServiceEnvelope();
        var tracking = TrackingTarget(envelope); var forge = new AfcTargetRef(tracking.ProviderId, tracking.TargetId, tracking.BindingId, tracking.Revision);
        var create = new AtcCreate("Typed issue", "Only the composite is external");
        var mutation = new AtcMutationRequest("create", Key('a'), AtcContract.Digest(tracking, create), create);
        var intent = new AfcCreateIntent("42", "Typed PR", new("agentstration/task", HeadRevision), new("main", HeadRevision));
        var pull = new AfcCreateRequest("create-pr", Key('b'), AfcContract.Digest(forge, intent), intent);
        await using (var extension = ContractExtension(fixture))
        {
            using var http = extension.CreateClient();
            var manifest = await new AepClient(http, expectedExtensionId: "Agentstration.Extensions.GitHub").DiscoverAsync();
            Assert.HasCount(0, AtcAepContributions.Validate(manifest)); Assert.HasCount(0, AfcAepContributions.Validate(manifest));
            var version = manifest.Contributions.ConnectedServiceTypes!.Single().Versions.Single();
            Assert.AreEqual(GitHub.GitHubServiceContract.TypeVersion.SchemaDigest, version.SchemaDigest);
            Assert.IsTrue(version.Inputs.Single(value => value.Id == "credential").Protection == Agentstration.Aep.Abstractions.AepValueProtection.Secured);
            await using var mcp = await ContractClientAsync(extension, envelope);
            var atc = await AtcClient.ConnectAsync(mcp, tracking, AtcSchemas.Declaration("github_atc"));
            var afc = await AfcClient.ConnectAsync(mcp, forge, AfcSchemas.Declaration("github_afc"));
            Assert.AreEqual(AtcRecoveryScope.Process, atc.Description.Capabilities.Recovery);
            Assert.IsFalse(atc.Description.Capabilities.Operations.Contains("transition"));
            var repository = await afc.ResolveAsync(new("resolve", ["main", "agentstration/task"]));
            Assert.AreEqual("42", repository.NativeId);
            Assert.AreEqual("main", (await afc.ResolveAsync(new("default-ref", []))).Refs.Single().Name);
            await Assert.ThrowsAsync<AtcException>(() => atc.ReadAsync(new("path", "../../outside")));
            await Assert.ThrowsAsync<AfcException>(() => afc.ReadAsync(new("path", "42", "../../outside")));
            fixture.Native.LoseIssueResponse = true; fixture.Native.LosePullResponse = true;
            Assert.AreEqual(AtcMutationStatus.Uncertain, (await atc.MutateAsync(mutation)).Status);
            Assert.AreEqual(AfcReceiptStatus.Uncertain, (await afc.CreateAsync(pull)).Status);
        }
        await using (var extension = ContractExtension(fixture))
        {
            await using var mcp = await ContractClientAsync(extension, envelope);
            var atc = await AtcClient.ConnectAsync(mcp, tracking, AtcSchemas.Declaration("github_atc"));
            var afc = await AfcClient.ConnectAsync(mcp, forge, AfcSchemas.Declaration("github_afc"));
            var issue = await atc.MutateAsync(mutation with { Mode = AtcMutationMode.Recover });
            Assert.AreEqual(AtcMutationStatus.Applied, issue.Status, issue.ErrorCode);
            Assert.AreEqual("Only the composite is external", issue.Item!.Body!.Split("\n", StringSplitOptions.None)[0]);
            fixture.Native.AddIssue("Parent"); fixture.Native.AddIssue("Dependency");
            fixture.Native.Issues[1].Labels.Add("unrelated");
            var observed = await atc.ReadAsync(new("observe-before-update", issue.Item.Identity.ItemId));
            var change = new AtcUpdate(issue.Item.Identity.ItemId, observed.Revision, AddClassifications: ["agentstration:planned"]);
            var updated = await atc.MutateAsync(new("labels", Key('c'), AtcContract.Digest(tracking, change), change));
            Assert.AreEqual(AtcMutationStatus.Applied, updated.Status, updated.ErrorCode);
            CollectionAssert.Contains(updated.Item!.Classifications.ToArray(), "unrelated");
            var relation = new AtcAddRelation(issue.Item.Identity.ItemId, updated.Item!.Revision, new(AtcRelationKind.BlockedBy, new(tracking.ProviderId, tracking.TargetId, "3")));
            var related = await atc.MutateAsync(new("relation", Key('d'), AtcContract.Digest(tracking, relation), relation));
            Assert.AreEqual(AtcMutationStatus.Applied, related.Status, related.ErrorCode);
            Assert.IsTrue(related.Item!.Relations.Any(value => value.Kind == AtcRelationKind.BlockedBy));
            var recovered = await afc.CreateAsync(pull with { Mode = AfcMutationMode.Recover });
            Assert.AreEqual(AfcReceiptStatus.Applied, recovered.Status, recovered.ErrorCode);
            Assert.AreEqual(recovered.ChangeRequest!.Identity.NativeId, (await afc.ReadAsync(new("read", "42", recovered.ChangeRequest.Identity.NativeId))).Identity.NativeId);
            fixture.Native.ReplacePullBody("Manual native edit");
            var changed = await afc.CreateAsync(pull with { Mode = AfcMutationMode.Recover });
            Assert.AreEqual(AfcReceiptStatus.Uncertain, changed.Status); Assert.AreEqual("github_native_state_changed", changed.ErrorCode);
            Assert.AreEqual(1, fixture.Native.IssuePosts); Assert.AreEqual(1, fixture.Native.PullPosts);
            foreach (var path in Directory.EnumerateFiles(fixture.Directory, "*.json", SearchOption.AllDirectories))
                Assert.IsFalse((await File.ReadAllTextAsync(path)).Contains(Credential, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task TypedServicesIsolateSubjectsTargetsAndRecoveryWithoutAccountOrVendorFallback()
    {
        using var fixture = new Fixture(); await using var extension = ContractExtension(fixture);
        var first = ServiceEnvelope(); var second = ServiceEnvelope("8", Credential + "-second", "43", "fixture/second");
        foreach (var envelope in new[] { first, second })
        {
            await using var mcp = await ContractClientAsync(extension, envelope);
            var client = await AtcClient.ConnectAsync(mcp, TrackingTarget(envelope), AtcSchemas.Declaration("github_atc"));
            Assert.AreEqual(envelope.Inputs["targetId"], client.Description.Target.TargetId);
        }
        var change = new AtcCreate("First account"); var target = TrackingTarget(first);
        await using (var mcp = await ContractClientAsync(extension, first))
        {
            var client = await AtcClient.ConnectAsync(mcp, target, AtcSchemas.Declaration("github_atc"));
            Assert.AreEqual(AtcMutationStatus.Applied, (await client.MutateAsync(new("create", Key('a'), AtcContract.Digest(target, change), change))).Status);
        }
        var otherAccount = ServiceEnvelope("8", Credential + "-second");
        await using (var mcp = await ContractClientAsync(extension, otherAccount))
        {
            var client = await AtcClient.ConnectAsync(mcp, target, AtcSchemas.Declaration("github_atc"));
            Assert.AreEqual(AtcMutationStatus.Missing, (await client.MutateAsync(new("recover", Key('a'), AtcContract.Digest(target, change), change, AtcMutationMode.Recover))).Status);
        }
        foreach (var invalid in new[] { ServiceEnvelope("999"), ServiceEnvelope(targetId: "999"), ServiceEnvelope(credential: "denied") })
        {
            await using var mcp = await ContractClientAsync(extension, invalid);
            await Assert.ThrowsAsync<AtcException>(() => AtcClient.ConnectAsync(mcp, TrackingTarget(invalid), AtcSchemas.Declaration("github_atc")));
        }
        using var noHeaders = extension.CreateClient();
        await using (var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new(noHeaders.BaseAddress!, "/mcp") }, noHeaders)))
        {
            Assert.IsTrue((await mcp.ListToolsAsync()).Any(tool => tool.Name == "github_atc_describe"));
            await Assert.ThrowsAsync<AtcException>(() => AtcClient.ConnectAsync(mcp, target, AtcSchemas.Declaration("github_atc")));
        }
        Assert.AreEqual(1, fixture.Native.IssuePosts);
    }

    private static WebApplicationFactory<GitHubExtension::Program> ContractExtension(Fixture fixture) =>
        new WebApplicationFactory<GitHubExtension::Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<GitHub.GitHubProviderOptions>(options => options.StateDirectory = Path.Combine(fixture.Directory, "typed-provider"));
            services.AddHttpClient<GitHub.GitHubConnection>().ConfigurePrimaryHttpMessageHandler(() => fixture.Native);
        }));
    private static GitHub.GitHubServiceEnvelope ServiceEnvelope(string subject = "7", string credential = Credential, string targetId = "42", string repository = Repository) =>
        new("github", "1.0.0", GitHub.GitHubServiceContract.TypeVersion.SchemaDigest,
            JsonSerializer.SerializeToElement(new GitHub.GitHubServiceOptions("https://api.github.com", repository, "agentstration:"), Json),
            new Dictionary<string, string>
            {
                ["providerId"] = GitHub.GitHubServiceContract.Authority("https://api.github.com"),
                ["targetId"] = targetId,
                ["bindingId"] = "fixture",
                ["revision"] = "revision-1",
                ["subjectId"] = subject,
                ["credential"] = credential
            });
    private static AtcTargetRef TrackingTarget(GitHub.GitHubServiceEnvelope envelope) => new(envelope.Inputs["providerId"], envelope.Inputs["targetId"], envelope.Inputs["bindingId"], envelope.Inputs["revision"]);
    private static async Task<McpClient> ContractClientAsync(WebApplicationFactory<GitHubExtension::Program> extension, GitHub.GitHubServiceEnvelope envelope)
    {
        var http = extension.CreateClient();
        http.DefaultRequestHeaders.Add(GitHub.GitHubServiceContract.Header, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(envelope, Json)));
        return await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: true));
    }
}
