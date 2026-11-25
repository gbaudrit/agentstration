using System.Net;
using System.Net.Http.Json;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task BootstrapFileClientCarriesTheReviewedDigestAndReturnsOriginalYaml()
    {
        BootstrapProfileFilesRequest? captured = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.AreEqual("/api/bootstrap/profiles/files", request.RequestUri!.AbsolutePath);
            captured = request.Content!.ReadFromJsonAsync<BootstrapProfileFilesRequest>().GetAwaiter().GetResult();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new BootstrapProfileFiles("nantes-alertes", "digest", ["20-secret.yaml"], "20-secret.yaml", "valueFrom: {configuration: fixture}")) };
        }))
        { BaseAddress = new("http://localhost/") };
        var selection = new BootstrapProfileFilesRequest("nantes-alertes", "20-secret.yaml", ExpectedDigest: "digest");
        var result = await new BootstrapProfilesApiClient(http).GetFilesAsync(selection, default);
        Assert.AreEqual(selection, captured);
        Assert.AreEqual("valueFrom: {configuration: fixture}", result.Yaml);
    }

    [TestMethod]
    public async Task BootstrapClientPropagatesOrderedProfilesTargetBindingsAndDigest()
    {
        ApplyBootstrapProfilesRequest? captured = null;
        var application = BootstrapApplication("application-1");
        var requestId = Guid.NewGuid();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request.Content!.ReadFromJsonAsync<ApplyBootstrapProfilesRequest>().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(application) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var target = new BootstrapApplicationTarget(Guid.NewGuid(), Guid.NewGuid());
        var binding = new BootstrapBindingSelection("base", "model", new("reasoning", @namespace: new("shared")));

        var actual = await new BootstrapProfilesApiClient(httpClient).ApplyAsync(
            new BootstrapProfileSelection(["base", "agents"], target, [binding]),
            "digest-1",
            CancellationToken.None, requestId);

        Assert.AreEqual(requestId, captured?.RequestId);
        Assert.AreEqual(application.Metadata.Name, actual.Metadata.Name);
        Assert.IsNotNull(captured);
        CollectionAssert.AreEqual(new[] { "base", "agents" }, captured.Profiles.ToArray());
        Assert.AreEqual(target, captured.Target);
        Assert.AreEqual("digest-1", captured.ExpectedDigest);
        Assert.AreEqual(binding, captured.Bindings!.Single());
    }

    [TestMethod]
    public async Task BootstrapClientMapsChangedDigestProblemDetails()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { title = "bootstrap_invalid", detail = "Preview the application again.", status = 400 })
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var error = await Assert.ThrowsAsync<AgentstrationApiException>(() =>
            new BootstrapProfilesApiClient(httpClient).ApplyAsync(new BootstrapProfileSelection(["base"]), "stale", CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.AreEqual("bootstrap_invalid", error.ProblemTitle);
    }

    [TestMethod]
    public async Task SeedClientPreservesTheExactPinAndTargetInPreviewAndApplication()
    {
        var target = new BootstrapApplicationTarget(Guid.NewGuid(), Guid.NewGuid());
        var pin = new WorkspaceSeedSelection(Guid.NewGuid(), new string('a', 64));
        var calls = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("preview", StringComparison.Ordinal))
            {
                var value = request.Content!.ReadFromJsonAsync<BootstrapProfilePreviewRequest>().GetAwaiter().GetResult()!;
                Assert.AreEqual(pin, value.Seed); Assert.AreEqual(target, value.Target); Assert.HasCount(0, value.Profiles);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new BootstrapCompositionPreview([], BootstrapProfileScope.Workspace, target, [], "digest", [])) };
            }
            var application = request.Content!.ReadFromJsonAsync<ApplyBootstrapProfilesRequest>().GetAwaiter().GetResult()!;
            Assert.AreEqual(pin, application.Seed); Assert.AreEqual(target, application.Target);
            return new(HttpStatusCode.Accepted) { Content = JsonContent.Create(BootstrapApplication("seed-application")) };
        }))
        { BaseAddress = new("http://localhost/") };
        var client = new BootstrapProfilesApiClient(http); var selection = new BootstrapProfileSelection([], target) { Seed = pin };
        _ = await client.PreviewAsync(selection, default);
        _ = await client.ApplyAsync(selection, "digest", default, Guid.NewGuid());
        Assert.AreEqual(2, calls);
    }

    private static BootstrapApplicationResource BootstrapApplication(string name) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = BootstrapKinds.BootstrapApplication,
        Metadata = new() { Name = name },
        Definition = new BootstrapApplicationProperties
        {
            ActorPrincipalId = Guid.NewGuid(),
            Profiles = ["base"],
            Scope = BootstrapProfileScope.Instance,
            Digest = "digest-1",
            StartedAt = DateTimeOffset.UnixEpoch
        }
    };
}
