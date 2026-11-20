using System.Net;
using System.Net.Http.Json;
using Agentstration.Security.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SecurityAuditCollectionTests
{
    private const string Actor = "11111111-1111-1111-1111-111111111111";
    private const string Target = "22222222-2222-2222-2222-222222222222";
    private const string Correlation = "correlation-0123456789-0123456789-0123456789";

    [TestMethod]
    [DataRow("en-US", "Recorded action", "Unknown", "up to 200")]
    [DataRow("fr-FR", "Action enregistrée", "Inconnu", "200 au maximum")]
    public async Task BoundedLatestSnapshotHasNoGlobalQueryAndKeepsCompleteCopyableFacts(string culture, string unknownAction, string unknownOutcome, string limitHelp)
    {
        using var locale = new TestCultureScope(culture);
        using var handler = new Handler(_ => Task.FromResult(Response(45)));
        using var context = Context(handler);
        var cut = context.Render<OrganizationSecurityAudit>();
        Assert.HasCount(45, cut.FindAll("[data-testid=security-audit-event]"));
        Assert.IsEmpty(cut.FindAll("input[type=search], [data-filter-key], .collection-pager, .collection-actions"));
        StringAssert.Contains(cut.Markup, limitHelp);
        StringAssert.Contains(cut.Markup, unknownAction);
        StringAssert.Contains(cut.Markup, unknownOutcome);
        StringAssert.Contains(cut.Markup, "new-audit-action");
        Assert.IsFalse(cut.Markup.Contains("AuditAction.new-audit-action", StringComparison.Ordinal));
        Assert.IsTrue(cut.FindAll(".copyable-value code").Any(value => value.TextContent == Actor));
        Assert.IsTrue(cut.FindAll(".copyable-value code").Any(value => value.TextContent == Target));
        Assert.IsTrue(cut.FindAll(".copyable-value code").Any(value => value.TextContent == Correlation));
        Assert.AreEqual(DateTimeOffset.UnixEpoch.ToString("O"), cut.Find("time").GetAttribute("datetime"));
        Assert.HasCount(5, cut.FindAll(".organization-tabs a"));
        await cut.FindAll(".collection-view-switch button")[1].ClickAsync(new());
        Assert.HasCount(45, cut.FindAll(".collection-card"));
        Assert.AreEqual(1, handler.Requests);
        await cut.Find("[data-testid=console-admin-organization-security-audit-refresh]").ClickAsync(new());
        Assert.HasCount(45, cut.FindAll(".collection-card"));
        Assert.AreEqual(2, handler.Requests);
    }

    [TestMethod]
    public async Task FailedRefreshRetainsSnapshotAndRetryReplacesItWithoutAFalseTotal()
    {
        using var handler = new Handler(call => call == 2 ? throw new HttpRequestException("Private transport detail") : Task.FromResult(Response(call == 1 ? 2 : 1)));
        using var context = Context(handler);
        var cut = context.Render<OrganizationSecurityAudit>();
        await cut.Find("[data-testid=console-admin-organization-security-audit-refresh]").ClickAsync(new());
        Assert.HasCount(2, cut.FindAll("[data-testid=security-audit-event]"));
        Assert.IsFalse(cut.Markup.Contains("Private transport detail", StringComparison.Ordinal));
        await cut.Find("[role=alert] button").ClickAsync(new());
        Assert.HasCount(1, cut.FindAll("[data-testid=security-audit-event]"));
        Assert.AreEqual(3, handler.Requests);
        Assert.IsEmpty(cut.FindAll(".collection-pager"));
    }

    [TestMethod]
    public async Task PrivilegeNotificationClearsCachedEventsBeforeReauthorizationAndRejectsLateResponses()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>();
        var replacement = new TaskCompletionSource<HttpResponseMessage>();
        using var handler = new Handler(call => call switch { 1 => Task.FromResult(Response(1)), 2 => old.Task, 3 => replacement.Task, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) });
        using var context = Context(handler);
        var cut = context.Render<OrganizationSecurityAudit>();
        var refresh = cut.Find("[data-testid=console-admin-organization-security-audit-refresh]").ClickAsync(new());
        await cut.InvokeAsync(() => context.Services.GetRequiredService<ConsoleContextState>().LoadAsync(CancellationToken.None));
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("[data-testid=security-audit-event]")));
        replacement.SetResult(Response(2));
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("[data-testid=security-audit-event]")));
        old.SetResult(Response(4));
        await refresh;
        Assert.HasCount(2, cut.FindAll("[data-testid=security-audit-event]"));
        await cut.Find("[data-testid=console-admin-organization-security-audit-refresh]").ClickAsync(new());
        Assert.IsEmpty(cut.FindAll("[data-testid=security-audit-event], .copyable-value"));
        StringAssert.Contains(cut.Markup, context.Services.GetRequiredService<IStringLocalizer<OrganizationStrings>>()["PlatformAdminRequiredMessage"].Value);
    }

    [TestMethod]
    public void ApiPlatformAdminPolicyIsAuthoritativeInsteadOfAnInventedResourceReadGate()
    {
        using var granted = new Handler(_ => Task.FromResult(Response(1)));
        using (var context = Context(granted))
        {
            var cut = context.Render<OrganizationSecurityAudit>();
            Assert.HasCount(1, cut.FindAll("[data-testid=security-audit-event]"));
            Assert.AreEqual(1, granted.Requests);
        }
        using var denied = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var rejected = Context(denied);
        var page = rejected.Render<OrganizationSecurityAudit>();
        Assert.IsEmpty(page.FindAll("[data-testid=security-audit-event]"));
        Assert.AreEqual(1, denied.Requests);
    }

    private static HttpResponseMessage Response(int count) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(Enumerable.Range(0, count).Select(index => new SecurityAuditEvent(Guid.NewGuid(),
            index % 2 == 0 ? "local-account.created" : "new-audit-action", index % 2 == 0 ? SecurityAuditOutcome.Succeeded : (SecurityAuditOutcome)999,
            Guid.Parse(Actor), Guid.Parse(Target), null,
            index % 3 == 0 ? Guid.Parse("44444444-4444-4444-4444-444444444444") : null,
            index % 2 == 0 ? Guid.Parse("55555555-5555-5555-5555-555555555555") : null,
            "fixture-reason", Correlation, DateTimeOffset.UnixEpoch)).ToArray())
    };
    private static BunitContext Context(Handler handler)
    {
        var provider = new ResourceListContextProvider { Permissions = new HashSet<string>() };
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext(provider);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IIdentityAdministrationApiClient>(new IdentityAdministrationApiClient(new HttpClient(handler, false) { BaseAddress = new("https://agentstration.test/") }));
        return context;
    }
    private sealed class Handler(Func<int, Task<HttpResponseMessage>> load) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/identity/audit-events?limit=200", request.RequestUri!.PathAndQuery);
            return load(++Requests);
        }
    }
}
