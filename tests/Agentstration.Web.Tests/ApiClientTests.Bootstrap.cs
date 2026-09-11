using System.Net;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task BootstrapClientPropagatesOrderedProfilesTargetBindingsAndDigest()
    {
        ApplyBootstrapProfilesRequest? captured = null;
        var application = BootstrapApplication("application-1");
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request.Content!.ReadFromJsonAsync<ApplyBootstrapProfilesRequest>().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(application) };
        })) { BaseAddress = new Uri("http://localhost/") };
        var target = new BootstrapApplicationTarget(Guid.NewGuid(), Guid.NewGuid());
        var binding = new BootstrapBindingSelection("base", "model", new("reasoning", @namespace: new("shared")));

        var actual = await new BootstrapProfilesApiClient(httpClient).ApplyAsync(
            new BootstrapProfileSelection(["base", "agents"], target, [binding]),
            "digest-1",
            CancellationToken.None);

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
        })) { BaseAddress = new Uri("http://localhost/") };

        var error = await Assert.ThrowsAsync<AgentstrationApiException>(() =>
            new BootstrapProfilesApiClient(httpClient).ApplyAsync(new BootstrapProfileSelection(["base"]), "stale", CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.AreEqual("bootstrap_invalid", error.ProblemTitle);
    }

    private static BootstrapApplicationResource BootstrapApplication(string name) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.BootstrapApplication,
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
