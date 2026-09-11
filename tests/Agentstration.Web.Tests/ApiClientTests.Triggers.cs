using System.Net;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task TriggerClientPreservesNamespaceAndEtagOnUpdate()
    {
        var trigger = Trigger("nightly", ResourceNamespace.Parse("ops"));
        HttpRequestMessage? captured = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(trigger),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"next\"") }
            };
        })) { BaseAddress = new Uri("http://localhost/") };

        var result = await new TriggerApiClient(httpClient).SaveAsync(trigger, "\"current\"", CancellationToken.None);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Put, captured.Method);
        Assert.AreEqual("/api/namespaces/ops/triggers/nightly", captured.RequestUri!.AbsolutePath);
        Assert.AreEqual("\"current\"", captured.Headers.IfMatch.Single().Tag);
        Assert.AreEqual("\"next\"", result.ETag);
    }

    [TestMethod]
    public async Task TriggerClientUsesAuthoritativeSchedulePreview()
    {
        TriggerSchedulePreviewRequest? captured = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request.Content!.ReadFromJsonAsync<TriggerSchedulePreviewRequest>().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new TriggerSchedulePreviewResponse([DateTimeOffset.UnixEpoch]))
            };
        })) { BaseAddress = new Uri("http://localhost/") };

        var values = await new TriggerApiClient(httpClient).PreviewScheduleAsync(
            new TriggerSchedule { Type = TriggerScheduleType.Interval, Every = "PT1H", StartAt = DateTimeOffset.UnixEpoch },
            5,
            CancellationToken.None);

        Assert.IsNotNull(captured);
        Assert.AreEqual(5, captured.Count);
        Assert.HasCount(1, values);
    }

    [TestMethod]
    public async Task TriggerClientMapsProblemDetailsForFailedRunNow()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { title = "trigger_disabled", detail = "The Trigger is disabled.", status = 409 })
        })) { BaseAddress = new Uri("http://localhost/") };

        var error = await Assert.ThrowsAsync<AgentstrationApiException>(() =>
            new TriggerApiClient(httpClient).RunNowAsync(ResourceNamespace.Default, "nightly", CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.Conflict, error.StatusCode);
        Assert.AreEqual("trigger_disabled", error.ProblemTitle);
    }

    private static TriggerResource Trigger(string name, ResourceNamespace @namespace) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.Trigger,
        Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
        Definition = new TriggerProperties
        {
            DisplayName = "Nightly",
            Source = new TriggerSource
            {
                Schedule = new TriggerSchedule { Type = TriggerScheduleType.Interval, Every = "PT1H", StartAt = DateTimeOffset.UnixEpoch }
            },
            Target = new TriggerTarget { Flow = new TriggerFlowTarget { Name = "flow" } }
        }
    };
}
