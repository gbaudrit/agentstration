using Agentstration.Sources.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BootstrapProgressComponentTests
{
    [TestMethod]
    [DataRow("en-US", "Processed 1 of 512 resources", "1 created", "Resource 2:")]
    [DataRow("fr-FR", "1 ressources traitées sur 512", "1 créée(s)", "Ressource 2 :")]
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
            StringAssert.Contains(panel.TextContent, created);
            StringAssert.Contains(panel.TextContent, current);
            Assert.AreEqual("512", panel.QuerySelector("progress")?.GetAttribute("max"));
            Assert.AreEqual("1", panel.QuerySelector("progress")?.GetAttribute("value"));
            StringAssert.Contains(panel.TextContent, "Parameter/bootstrap-load-test-002");
        });
        Assert.AreEqual(0, client.Submissions);
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
    public void CancelRequestsCancellationForExistingIdentity()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StubClient();
        using var context = Context(client);
        var rendered = context.Render<BootstrapProfiles>();
        rendered.FindAll("button").Single(button => button.TextContent == "Cancel application").Click();
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "Cancellation requested."));
        Assert.AreEqual("retained-application", client.CancelledId);
        Assert.AreEqual(0, client.Submissions);
    }

    private static BunitContext Context(StubClient client)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IBootstrapProfilesApiClient>(client);
        return context;
    }

    private sealed class StubClient : IBootstrapProfilesApiClient
    {
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
                Resources = [new("load-test", "10-parameters.yaml#1", "Parameter", "bootstrap-load-test-001", BootstrapResourceDisposition.Create)],
                CurrentResource = new("load-test", "10-parameters.yaml#2", "Parameter", "bootstrap-load-test-002", BootstrapResourceDisposition.Create)
            }
        };
        public bool DisconnectOnce { get; init; }
        public int Submissions { get; private set; }
        public int Observations { get; private set; }
        public string? CancelledId { get; private set; }
        public Task<BootstrapManagementView> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new BootstrapManagementView(new(null, false, [], []), [], [], [application]));
        public Task<BootstrapApplicationResource?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken)
        {
            Assert.AreEqual(application.Metadata.Name, applicationId);
            Observations++;
            if (DisconnectOnce && Observations == 1) throw new HttpRequestException("Synthetic disconnect");
            if (!DisconnectOnce) return Task.FromResult<BootstrapApplicationResource?>(application);
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
            throw new NotSupportedException();
        }
        public Task<BootstrapProfileSummary> GetSourceProfileAsync(BootstrapSourceProfileSelection selection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<BootstrapBindingTargetOption>> GetBindingTargetsAsync(BootstrapBindingTargetsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BootstrapCompositionPreview> PreviewAsync(BootstrapProfileSelection selection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
