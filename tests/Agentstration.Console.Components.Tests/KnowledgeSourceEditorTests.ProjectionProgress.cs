using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    [DataRow("en-US", "Projection in progress")]
    [DataRow("fr-FR", "Projection en cours")]
    public async Task ProjectionShowsProgressAndRefreshesWhileTheStartRequestIsPending(string language, string label)
    {
        using var culture = new TestCultureScope(language);
        var source = ProjectedSource();
        var client = new KnowledgeClientStub(source);
        var completed = (await client.StartProjectionAsync(source.Namespace, source.Name, new())).Value;
        client.Projections.Clear();
        var completion = new TaskCompletionSource<ResourceSnapshot<KnowledgeProjectionResource>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ProjectionStarter = (request, token) =>
        {
            client.Projections.Add(completed with { State = KnowledgeProjectionState.Running, CompletedAt = null, IdempotencyKey = request.IdempotencyKey });
            return completion.Task.WaitAsync(token);
        };
        await using var context = CreateContext(client);
        var clock = new ProjectionClock();
        context.Services.AddSingleton<TimeProvider>(clock);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/knowledge-sources/" + source.Name + "?view=projections");
        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters.Add(value => value.Name, source.Name));
        var start = rendered.Find("[data-testid='knowledge-start-projection']").ClickAsync(new MouseEventArgs());
        rendered.WaitForAssertion(() => Assert.Contains(label, rendered.Find("[data-testid='knowledge-start-projection']").TextContent));
        Assert.IsNotNull(rendered.Find("[data-testid='knowledge-projection-progress'] progress"));
        Assert.IsTrue(rendered.Find("[data-testid='knowledge-start-projection']").HasAttribute("disabled"));
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() => Assert.AreEqual(1, rendered.FindAll("[data-testid='knowledge-projection-row']").Count));
        Assert.AreEqual("_blank", rendered.Find("[data-testid='knowledge-projection-row'] a[href*='/flow-runs/']").GetAttribute("target"));
        Assert.AreEqual(2, client.ProjectionStarts); // Includes fixture preparation; polling must never start a second request.
        client.Projections[0] = completed;
        client.Snapshots = [ExistingSnapshot()];
        completion.SetResult(new(completed, "etag-completed"));
        await start;
        rendered.WaitForAssertion(() => Assert.AreEqual(0, rendered.FindAll("[data-testid='knowledge-projection-progress']").Count));
        Assert.IsFalse(rendered.Find("[data-testid='knowledge-start-projection']").HasAttribute("disabled"));
        rendered.Find("[data-testid='knowledge-source-snapshots-tab']").Click();
        Assert.Contains(ExistingSnapshot().Snapshot.Name, rendered.Markup);
        await context.DisposeAsync();
        Assert.IsTrue(clock.TimerDisposed);
    }

    [TestMethod]
    public async Task FailedProjectionStillRefreshesHistoryAndAllowsAnExplicitRetry()
    {
        using var culture = new TestCultureScope("en-US");
        var source = ProjectedSource();
        var client = new KnowledgeClientStub(source);
        var failed = (await client.StartProjectionAsync(source.Namespace, source.Name, new())).Value with
        { State = KnowledgeProjectionState.Failed, ErrorCode = "processor_failed", ErrorMessage = "Processing failed." };
        client.Projections.Clear();
        client.ProjectionStarter = (_, _) =>
        {
            client.Projections.Add(failed);
            throw new HttpRequestException("The projection request failed.");
        };
        await using var context = CreateContext(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/knowledge-sources/" + source.Name + "?view=projections");
        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters.Add(value => value.Name, source.Name));
        await rendered.Find("[data-testid='knowledge-start-projection']").ClickAsync(new MouseEventArgs());
        rendered.WaitForAssertion(() => Assert.AreEqual(1, rendered.FindAll("[data-testid='knowledge-projection-row']").Count));
        Assert.Contains("The projection request failed.", rendered.Markup);
        Assert.IsFalse(rendered.Find("[data-testid='knowledge-start-projection']").HasAttribute("disabled"));
        Assert.AreEqual(0, rendered.FindAll("[data-testid='knowledge-projection-progress']").Count);
        Assert.AreEqual(2, client.ProjectionStarts);
    }

    [TestMethod]
    public async Task ExistingProjectionRecoversFromRefreshFailureAndUpdatesAfterCompletion()
    {
        using var culture = new TestCultureScope("en-US");
        var source = ProjectedSource();
        var client = new KnowledgeClientStub(source);
        var completed = (await client.StartProjectionAsync(source.Namespace, source.Name, new())).Value;
        client.Projections[0] = completed with { State = KnowledgeProjectionState.Running, CompletedAt = null };
        await using var context = CreateContext(client);
        var clock = new ProjectionClock();
        context.Services.AddSingleton<TimeProvider>(clock);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/knowledge-sources/" + source.Name + "?view=projections");
        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters.Add(value => value.Name, source.Name));
        Assert.IsNotNull(rendered.Find("[data-testid='knowledge-projection-progress']"));
        client.RejectProjectionRefresh = true;
        var reads = client.ProjectionReads;
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() => Assert.IsTrue(client.ProjectionReads > reads));
        rendered.WaitForAssertion(() => Assert.Contains("refresh", rendered.Find("[role='tabpanel']").TextContent.ToLowerInvariant()));
        client.RejectProjectionRefresh = false;
        client.Projections[0] = completed;
        await rendered.InvokeAsync(clock.Tick);
        rendered.WaitForAssertion(() => Assert.AreEqual(0, rendered.FindAll("[data-testid='knowledge-projection-progress']").Count));
        Assert.IsFalse(rendered.Find("[data-testid='knowledge-start-projection']").HasAttribute("disabled"));
        Assert.AreEqual(1, client.ProjectionStarts);
    }

    [TestMethod]
    public async Task FailedProjectionResponseDoesNotDisplayACompletionMessage()
    {
        using var culture = new TestCultureScope("en-US");
        var source = ProjectedSource();
        var client = new KnowledgeClientStub(source);
        var failed = (await client.StartProjectionAsync(source.Namespace, source.Name, new())).Value with
        { State = KnowledgeProjectionState.Failed, ErrorMessage = "Anonymization failed." };
        client.Projections.Clear();
        client.ProjectionStarter = (_, _) => { client.Projections.Add(failed); return Task.FromResult(new ResourceSnapshot<KnowledgeProjectionResource>(failed, "etag")); };
        await using var context = CreateContext(client);
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/knowledge-sources/" + source.Name + "?view=projections");
        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters.Add(value => value.Name, source.Name));
        await rendered.Find("[data-testid='knowledge-start-projection']").ClickAsync(new MouseEventArgs());
        Assert.Contains("Anonymization failed.", rendered.Markup);
        Assert.DoesNotContain("The projection completed", rendered.Markup);
        Assert.IsFalse(rendered.Find("[data-testid='knowledge-start-projection']").HasAttribute("disabled"));
    }

    private sealed class ProjectionClock : TimeProvider
    {
        private ProjectionTimer? timer;
        public bool TimerDisposed => timer?.Disposed == true;
        public void Tick() => timer?.Tick();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => timer = new(callback, state);
        private sealed class ProjectionTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Tick() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
