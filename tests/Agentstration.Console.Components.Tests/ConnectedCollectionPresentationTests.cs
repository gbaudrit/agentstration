using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ConnectedCollectionPresentationTests
{
    [TestMethod]
    [DataRow("en-US", "Read-only in this scope", "Ready")]
    [DataRow("fr-FR", "Lecture seule dans ce périmètre", "Prêt")]
    public void HomonymousServicesRetainExactIdentityContextAndIndependentGovernance(string locale, string inheritedLabel, string readyLabel)
    {
        using var culture = new TestCultureScope(locale);
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var inherited = Service(ResourceScopeRef.Instance, "parent-space", false);
        var owned = Service(workspace, "own-space", false);
        var managed = Service(workspace, "managed-space", true);
        using var handler = new ServiceListHandler([inherited, owned, managed]);
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext(workspace));
        context.Services.AddSingleton<IConnectedServicesClient>(new ConnectedServicesApiClient(http));

        var rendered = context.Render<Agentstration.Web.Components.Pages.ConnectedServices>();
        rendered.WaitForAssertion(() => Assert.HasCount(3, rendered.FindAll("[data-testid=connected-service-row]")));
        var rows = rendered.FindAll("[data-testid=connected-service-row]");
        for (var index = 0; index < rows.Count; index++)
        {
            var resource = new[] { inherited, owned, managed }[index];
            var row = rows[index];
            var link = row.QuerySelector("[data-testid=connected-service-name-link]")!;
            var destination = new Uri("https://agentstration.test" + link.GetAttribute("href"));
            var query = QueryHelpers.ParseQuery(destination.Query);
            Assert.AreEqual("/connected-services/same-name", destination.AbsolutePath);
            Assert.AreEqual(resource.Namespace.Value, query["namespace"].ToString());
            Assert.AreEqual(resource.ScopeRef!.Value.Value, query["scopeRef"].ToString());
            Assert.AreEqual(workspace.Value, query["context"].ToString());
            var qualified = row.QuerySelector(".copyable-value code")!;
            Assert.AreEqual($"{resource.Namespace}/same-name", qualified.GetAttribute("title"));
            Assert.IsNotNull(row.QuerySelector(".resource-icon"));
            Assert.IsNotNull(row.QuerySelector($".status-icon-only button[aria-label='{readyLabel}']"));
        }
        Assert.AreEqual(inheritedLabel, rows[0].QuerySelector(".managed-resource-badge button")!.GetAttribute("aria-label"));
        Assert.IsNull(rows[1].QuerySelector(".managed-resource-badge"));
        Assert.IsNotNull(rows[2].QuerySelector(".managed-resource-badge"));
        Assert.HasCount(0, rendered.FindAll(".collection-actions-menu"));
        Assert.AreEqual(1, handler.Requests);

        rendered.Find(".collection-view-switch button[aria-pressed=false]").Click();
        Assert.HasCount(3, rendered.FindAll("li[data-testid=connected-service-row]"));
        Assert.AreEqual(1, handler.Requests, "Changing presentation must not refetch the resources or their owner names.");
    }

    private static ConnectedServiceResource Service(ResourceScopeRef owner, string resourceNamespace, bool managed) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ConnectedServiceResourceKinds.ConnectedService,
        ScopeRef = owner,
        Metadata = new() { Name = "same-name", Namespace = ResourceNamespace.Parse(resourceNamespace) },
        Governance = managed ? ResourceGovernance.Core("service.fixture") : ResourceGovernance.User,
        Definition = new()
        {
            DisplayName = "Service " + new string('x', 120),
            Type = new() { Type = new("http-endpoint", ResourceScopeRef.Instance), TypeUid = Guid.NewGuid(), Version = "2.1.0", SchemaDigest = "sha256:fixture" },
            Options = JsonSerializer.SerializeToElement(new { })
        },
        ObservedState = new() { Availability = ConnectedServiceAvailability.Ready, ObservedAt = DateTimeOffset.UnixEpoch }
    };

    private sealed class ServiceListHandler(IReadOnlyList<ConnectedServiceResource> resources) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/connected-services", request.RequestUri!.AbsolutePath);
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resources) });
        }
    }

    private sealed class NavigationContext(ResourceScopeRef scope) : IConsoleNavigationContext
    {
        public string? ScopeRef => scope.Value;
        public string AddToUri(string uri) => QueryHelpers.AddQueryString(uri, "context", scope.Value);
    }
}
