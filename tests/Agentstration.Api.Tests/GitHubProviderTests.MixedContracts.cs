extern alias GitHubExtension;
extern alias AtcSample;
using System.Net.Http.Headers;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Client;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Agentstration.Web.Tests;

public sealed partial class GitHubProviderTests
{
    [TestMethod]
    public async Task GitHubForgePairsWithAnIndependentNonRepositoryTrackingProvider()
    {
        using var fixture = new Fixture(); await using var github = ContractExtension(fixture);
        await using var tracker = new WebApplicationFactory<AtcSample::Atc.Samples.LocalTracking.Program>();
        using var trackingHttp = tracker.CreateClient();
        var access = tracker.Services.GetRequiredService<AtcSample::Atc.Samples.LocalTracking.SampleAccess>();
        trackingHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        await using var trackingMcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new(trackingHttp.BaseAddress!, "/mcp") }, trackingHttp));
        await using var forgeMcp = await ContractClientAsync(github, ServiceEnvelope());
        var atc = await AtcClient.ConnectAsync(trackingMcp, access.Team);
        var service = ServiceEnvelope(); var target = TrackingTarget(service);
        var afc = await AfcClient.ConnectAsync(forgeMcp, new(target.ProviderId, target.TargetId, target.BindingId, target.Revision), AfcSchemas.Declaration("github_afc"));
        var change = new AtcCreate("Independent team backlog");
        var issue = await atc.MutateAsync(new("mixed-create", Key('a'), AtcContract.Digest(access.Team, change), change));
        Assert.AreEqual(AtcMutationStatus.Applied, issue.Status);
        var repository = await afc.ResolveAsync(new("github-resolve", ["main"]));
        Assert.AreNotEqual(issue.Item!.Identity.ProviderId, repository.ProviderId);
        Assert.AreEqual("42", repository.NativeId); Assert.AreEqual(0, fixture.Native.IssuePosts);
    }
}
