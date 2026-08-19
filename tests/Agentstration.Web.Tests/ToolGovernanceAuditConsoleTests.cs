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
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new ToolGovernanceAuditApiClient(http);

        await client.GetAsync(
            ToolExecutionOwnerKind.RuntimeRun,
            "run/1",
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
    }

    [TestMethod]
    public void PageRendersAttemptAndExplainsWhenArgumentsWereNotRetained()
    {
        using var context = new BunitContext();
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
            Assert.Contains("Arguments were not retained", rendered.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("provider-result", rendered.Markup, StringComparison.OrdinalIgnoreCase);
        });
    }

    [TestMethod]
    public void PageRendersRetainedInvocationArguments()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IToolGovernanceAuditClient>(new FakeAuditClient("{\"query\":\"latest dotnet version\"}"));

        var rendered = context.Render<ToolGovernanceAudit>(parameters => parameters
            .Add(value => value.Owner, "runtime")
            .Add(value => value.RunId, "run-1"));

        rendered.WaitForAssertion(() =>
        {
            Assert.Contains("latest dotnet version", rendered.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Arguments were not retained", rendered.Markup, StringComparison.Ordinal);
        });
    }

    private sealed class FakeAuditClient(string? arguments = null) : IToolGovernanceAuditClient
    {
        public Task<ToolGovernanceAuditPage> GetAsync(
            ToolExecutionOwnerKind ownerKind,
            string runId,
            long afterSequence,
            int limit,
            ToolGovernanceAuditFilters filters,
            CancellationToken cancellationToken) => Task.FromResult(new ToolGovernanceAuditPage(
                [new ToolGovernanceAuditRecord
                {
                    OwnerKind = ownerKind,
                    RunId = runId,
                    Sequence = 4,
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
                null));
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
