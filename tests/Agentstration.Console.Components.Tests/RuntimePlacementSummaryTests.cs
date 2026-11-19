using System.Net;
using System.Net.Http.Json;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class RuntimePlacementSummaryTests
{
    private static readonly ResourceScopeRef Owner = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [TestMethod]
    public void HiddenRetainedPlacementDoesNotReadAgainAndReturningRefreshesTheSameExecution()
    {
        using var handler = new Handler(); using var context = Context(handler);
        var rendered = context.Render<RuntimePlacementSummary>(p => p.Add(value => value.TargetKind, RuntimeAssignmentTargetKind.FlowRun).Add(value => value.RunId, "run-a").Add(value => value.ScopeRef, Owner));
        rendered.WaitForElement("[data-testid=runtime-placement]");
        Assert.AreEqual(1, handler.Reads);
        rendered.Render(p => p.Add(value => value.Active, false)); rendered.Render();
        Assert.AreEqual(1, handler.Reads);
        rendered.Render(p => p.Add(value => value.Active, true));
        rendered.WaitForAssertion(() => Assert.AreEqual(2, handler.Reads));
        Assert.AreEqual(Owner.Value, handler.RequestedScope);
        Assert.AreEqual("run-a", rendered.Find("[data-testid=runtime-placement]").GetAttribute("data-run-id"));
    }

    [TestMethod]
    public async Task SupersededPlacementReplyCannotReplaceAnotherExecution()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler { Loader = id => id == "run-a" ? pending.Task : Task.FromResult(Reply(id)) };
        using var context = Context(handler);
        var rendered = context.Render<RuntimePlacementSummary>(p => p.Add(value => value.TargetKind, RuntimeAssignmentTargetKind.FlowRun).Add(value => value.RunId, "run-a").Add(value => value.ScopeRef, Owner));
        rendered.Render(p => p.Add(value => value.RunId, "run-b"));
        rendered.WaitForElement("[data-testid=runtime-placement][data-run-id=run-b]");
        await rendered.InvokeAsync(() => pending.SetResult(Reply("run-a")));
        Assert.AreEqual("run-b", rendered.Find("[data-testid=runtime-placement]").GetAttribute("data-run-id"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailureOrMismatchedIdentityCannotRenderAnotherAssignmentsData(bool denied)
    {
        using var handler = new Handler { Loader = _ => Task.FromResult(denied ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { title = "Forbidden", detail = "Fixture denial" }) } : Reply("other-run")) };
        using var context = Context(handler);
        var rendered = context.Render<RuntimePlacementSummary>(p => p.Add(value => value.TargetKind, RuntimeAssignmentTargetKind.FlowRun).Add(value => value.RunId, "run-a").Add(value => value.ScopeRef, Owner));
        rendered.WaitForElement(".form-alert-warning[role=status]");
        Assert.IsEmpty(rendered.FindAll("[data-testid=runtime-placement]"));
        Assert.DoesNotContain("other-run", rendered.Markup);
        Assert.AreEqual(1, handler.Reads);
    }

    private static BunitContext Context(Handler handler)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IRuntimeApiClient>(new RuntimeApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost/") }));
        return context;
    }
    private static HttpResponseMessage Reply(string id) => new(HttpStatusCode.OK) { Content = JsonContent.Create(new RuntimeAssignmentPlacementResponse(Guid.NewGuid(), "FlowRun", id, "Succeeded", "microsoft-agent-framework", "1.0", "1.0", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], Owner)) };
    private sealed class Handler : HttpMessageHandler
    {
        public int Reads { get; private set; }
        public string? RequestedScope { get; private set; }
        public Func<string, Task<HttpResponseMessage>>? Loader { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++; Assert.AreEqual(HttpMethod.Get, request.Method);
            RequestedScope = QueryHelpers.ParseQuery(request.RequestUri!.Query)["scopeRef"].ToString();
            var id = request.RequestUri.Segments.Last();
            return Loader?.Invoke(id) ?? Task.FromResult(Reply(id));
        }
    }
}
