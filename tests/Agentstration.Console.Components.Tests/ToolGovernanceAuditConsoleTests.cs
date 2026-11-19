using System.Net;
using System.Text;
using System.Text.Json;
using Agentstration.Runtime.Abstractions;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolGovernanceAuditConsoleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task HttpClientEncodesDeepFilters()
    {
        Uri? requested = null;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            requested = request.RequestUri;
            var json = JsonSerializer.Serialize(new ToolGovernanceAuditPage([], null), JsonOptions);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ToolGovernanceAuditApiClient(http);

        await client.GetAsync(
            ToolExecutionOwnerKind.RuntimeRun,
            "run/1",
            ResourceScopeRef.Instance,
            12,
            50,
            new ToolGovernanceAuditFilters
            {
                ToolCallId = "logical call",
                InvocationId = "attempt/2",
                ToolId = "lookup",
                HookId = "managed:guard",
                ResourceGeneration = 7,
                Decision = ToolExecutionHookEvaluationKind.Denied
            },
            default);

        Assert.IsNotNull(requested);
        Assert.AreEqual("/api/tool-governance/runtime/run%2F1", requested.AbsolutePath);
        Assert.Contains("afterSequence=12", requested.Query, StringComparison.Ordinal);
        Assert.Contains("toolCallId=logical%20call", requested.Query, StringComparison.Ordinal);
        Assert.Contains("invocationId=attempt%2F2", requested.Query, StringComparison.Ordinal);
        Assert.Contains("resourceGeneration=7", requested.Query, StringComparison.Ordinal);
        Assert.Contains("decision=denied", requested.Query, StringComparison.Ordinal);
        Assert.Contains("scopeRef=%2Finstance", requested.Query, StringComparison.Ordinal);
    }

    [TestMethod]
    public void PageRendersAttemptAndExplainsWhenArgumentsWereNotRetained()
    {
        using var context = AuditContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolGovernanceAuditClient>(new FakeAuditClient());

        var rendered = context.Render<ToolGovernanceAudit>(parameters => parameters
            .Add(value => value.Owner, "runtime")
            .Add(value => value.RunId, "run-1"));
        rendered.WaitForAssertion(() =>
        {
            Assert.Contains("logical-call", rendered.Markup, StringComparison.Ordinal);
            Assert.Contains("attempt-1", rendered.Markup, StringComparison.Ordinal);
            Assert.Contains("default/ToolExecutionHook/guard", rendered.Markup, StringComparison.Ordinal);
            Assert.Contains(">7<", rendered.Markup, StringComparison.Ordinal);
            Assert.Contains("Denied", rendered.Markup, StringComparison.Ordinal);
            Assert.IsFalse(string.IsNullOrWhiteSpace(rendered.Find(".governance-invocation .muted").TextContent));
            Assert.DoesNotContain("provider-result", rendered.Markup, StringComparison.OrdinalIgnoreCase);
        });
    }

    [TestMethod]
    public void PageRendersRetainedInvocationArguments()
    {
        using var context = AuditContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolGovernanceAuditClient>(new FakeAuditClient("{\"query\":\"latest dotnet version\"}"));

        var rendered = context.Render<ToolGovernanceAudit>(parameters => parameters
            .Add(value => value.Owner, "runtime")
            .Add(value => value.RunId, "run-1"));

        rendered.WaitForAssertion(() =>
        {
            Assert.Contains("latest dotnet version", rendered.Markup, StringComparison.Ordinal);
            Assert.IsEmpty(rendered.FindAll(".governance-invocation .muted"));
        });
    }

    [TestMethod]
    public void AuditNavigationRetainsDeepFiltersAndUsesTheRealSequenceContinuation()
    {
        using var context = AuditContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeAuditClient(nextSequence: 4);
        context.Services.AddSingleton<IToolGovernanceAuditClient>(client);
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/?toolCallId=deep-call#audit");
        var rendered = context.Render<ToolGovernanceAudit>(parameters => parameters
            .Add(value => value.Owner, "flow").Add(value => value.RunId, "flow-run-exact"));
        rendered.WaitForElement(".governance-record");
        Assert.AreEqual("deep-call", client.LastFilters!.ToolCallId);
        Assert.AreEqual(1, client.ReadCount);
        rendered.Find(".governance-filters input").Change("pending-call");
        navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#overview");
        rendered.WaitForAssertion(() => Assert.Contains("flow-run-exact", rendered.Find(".resource-identity-summary").TextContent));
        navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#audit");
        Assert.AreEqual("pending-call", rendered.Find(".governance-filters input").GetAttribute("value"));
        Assert.AreEqual(1, client.ReadCount);
        rendered.Find(".governance-filters .button-primary").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual("pending-call", client.LastFilters!.ToolCallId));
        rendered.Find(".load-more button").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual(2, rendered.FindAll(".governance-record").Count));
        Assert.AreEqual(4, client.AfterSequence);
        Assert.AreEqual(50, client.Limit);
        Assert.AreEqual(3, client.ReadCount);
        Assert.IsEmpty(rendered.FindAll(".load-more"));
    }

    [TestMethod]
    public async Task SupersededExecutionReadCannotReplaceTheCurrentAudit()
    {
        using var context = AuditContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var first = new TaskCompletionSource<ToolGovernanceAuditPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DeferredAuditClient(first.Task);
        context.Services.AddSingleton<IToolGovernanceAuditClient>(client);
        var rendered = context.Render<ToolGovernanceAudit>(parameters => parameters
            .Add(value => value.Owner, "flow").Add(value => value.RunId, "first-run"));
        rendered.Render(parameters => parameters.Add(value => value.Owner, "flow").Add(value => value.RunId, "second-run"));
        rendered.WaitForAssertion(() => Assert.Contains("second-run", rendered.Find(".governance-record h2").TextContent));
        await rendered.InvokeAsync(() => first.SetResult(AuditPage("first-run")));
        rendered.WaitForAssertion(() => Assert.Contains("second-run", rendered.Find(".governance-record h2").TextContent));
        Assert.DoesNotContain("first-run", rendered.Find(".governance-record").TextContent);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    public void LosingTheAuditDuringContinuationClearsRetainedArgumentsAndCursor(HttpStatusCode status)
    {
        using var context = AuditContext(); context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeAuditClient("private retained invocation fixture", nextSequence: 4); context.Services.AddSingleton<IToolGovernanceAuditClient>(client);
        var cut = context.Render<ToolGovernanceAudit>(p => p.Add(c => c.Owner, "runtime").Add(c => c.RunId, "run-1"));
        Assert.Contains("private retained invocation fixture", cut.Find(".governance-invocation").TextContent);
        client.Error = new AgentstrationApiException("Audit unavailable", "fixture", status);
        cut.Find(".load-more button").Click();
        Assert.IsEmpty(cut.FindAll(".governance-record, .load-more"));
        Assert.DoesNotContain("private retained invocation fixture", cut.Markup); Assert.AreEqual(2, client.ReadCount);
    }

    [TestMethod]
    public void RecoverableContinuationFailureRetainsRecordsAndRetriesTheRealCursor()
    {
        using var context = AuditContext(); context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeAuditClient(nextSequence: 4); context.Services.AddSingleton<IToolGovernanceAuditClient>(client);
        var cut = context.Render<ToolGovernanceAudit>(p => p.Add(c => c.Owner, "flow").Add(c => c.RunId, "run-1"));
        client.Error = new AgentstrationApiException("Try again", "fixture", HttpStatusCode.ServiceUnavailable);
        cut.Find(".load-more button").Click(); Assert.HasCount(1, cut.FindAll(".governance-record"));
        Assert.AreEqual(4L, client.AfterSequence); Assert.IsNotNull(cut.Find(".load-more button"));
        client.Error = null; cut.Find(".load-more button").Click();
        Assert.HasCount(2, cut.FindAll(".governance-record")); Assert.AreEqual(4L, client.AfterSequence); Assert.AreEqual(50, client.Limit);
    }

    private static ToolGovernanceAuditPage AuditPage(string runId) => new([new ToolGovernanceAuditRecord
    {
        OwnerKind = ToolExecutionOwnerKind.FlowRun, RunId = runId, Sequence = 1, Timestamp = DateTimeOffset.UnixEpoch,
        ToolCallId = "logical-call", InvocationId = "attempt", ToolId = "lookup", ToolName = runId
    }], null);

    private sealed class DeferredAuditClient(Task<ToolGovernanceAuditPage> first) : IToolGovernanceAuditClient
    {
        public Task<ToolGovernanceAuditPage> GetAsync(ToolExecutionOwnerKind ownerKind, string runId, ResourceScopeRef? scopeRef,
            long afterSequence, int limit, ToolGovernanceAuditFilters filters, CancellationToken cancellationToken) =>
            runId == "first-run" ? first : Task.FromResult(AuditPage(runId));
    }

    [TestMethod]
    public void UnsupportedOwnerDoesNotQueryOrMasqueradeAsARuntimeExecution()
    {
        using var context = AuditContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FakeAuditClient();
        context.Services.AddSingleton<IToolGovernanceAuditClient>(client);
        var rendered = context.Render<ToolGovernanceAudit>(p => p.Add(value => value.Owner, "unsupported").Add(value => value.RunId, "run-unknown"));
        var strings = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<ToolGovernanceAuditStrings>>();
        rendered.WaitForAssertion(() => Assert.AreEqual(strings["UnknownOwner"].Value, rendered.Find(".resource-identity-summary dd").TextContent));
        Assert.AreEqual(0, client.ReadCount);
    }

    private static BunitContext AuditContext()
    {
        var context = new BunitContext().WithCollectionPresentation();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult("http://localhost/#audit");
        module.Setup<string>("navigate", _ => true).SetResult("http://localhost/#audit");
        return context;
    }

    private sealed class FakeAuditClient(string? arguments = null, long? nextSequence = null) : IToolGovernanceAuditClient
    {
        public int ReadCount { get; private set; }
        public Exception? Error { get; set; }
        public long AfterSequence { get; private set; }
        public int Limit { get; private set; }
        public ToolGovernanceAuditFilters? LastFilters { get; private set; }
        public Task<ToolGovernanceAuditPage> GetAsync(
            ToolExecutionOwnerKind ownerKind,
            string runId,
            ResourceScopeRef? scopeRef,
            long afterSequence,
            int limit,
            ToolGovernanceAuditFilters filters,
            CancellationToken cancellationToken)
        {
            ReadCount++; AfterSequence = afterSequence; Limit = limit; LastFilters = filters;
            if (Error is { } error) return Task.FromException<ToolGovernanceAuditPage>(error);
            return Task.FromResult(new ToolGovernanceAuditPage(
                [new ToolGovernanceAuditRecord
                {
                    OwnerKind = ownerKind,
                    RunId = runId,
                    Sequence = afterSequence + 4,
                    Timestamp = DateTimeOffset.UnixEpoch,
                    ToolCallId = "logical-call",
                    InvocationId = "attempt-1",
                    ToolId = "lookup",
                    ToolName = "Lookup",
                    ProviderId = "provider",
                    Arguments = arguments,
                    Evaluations = [new ToolExecutionHookEvaluation(
                        new ToolExecutionHookIdentity(
                            "managed:guard",
                            10,
                            ToolExecutionHookSource.Managed,
                            "default/ToolExecutionHook/guard",
                            7),
                        ToolExecutionHookEvaluationKind.Denied,
                        "blocked")]
                }],
                afterSequence == 0 ? nextSequence : null));
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
