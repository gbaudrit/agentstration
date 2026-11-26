using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class CollectionActionsLifecycleTests
{
    [TestMethod]
    public void DestructiveActionsAreOptionalAndHidingThemAlsoRemovesTheirSeparator()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/collection-ui.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization();
        var cut = context.Render<CollectionActions>(p => p.Add(c => c.ResourceName, "Resource")
            .Add(c => c.Secondary, "<a href='/resource'>Open</a>"));
        Assert.IsEmpty(cut.FindAll("hr, .collection-destructive-actions"));
        cut.Render(p => p.Add(c => c.Destructive, "<button type='button'>Delete</button>"));
        Assert.HasCount(1, cut.FindAll("hr"));
        Assert.AreEqual("Delete", cut.Find(".collection-destructive-actions button").TextContent);
        cut.Render(p => p.Add(c => c.ShowDestructiveActions, false));
        Assert.IsEmpty(cut.FindAll("hr, .collection-destructive-actions, button"));
        Assert.HasCount(1, cut.FindAll(".collection-actions-menu"));
        Assert.HasCount(1, cut.FindAll("a[href='/resource']"));
    }

    [TestMethod]
    public void HiddenDestructiveOnlyActionsLeaveNoEmptyMenuAndCanBeEnabledAgain()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/collection-ui.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization();
        var cut = context.Render<CollectionActions>(p => p.Add(c => c.ResourceName, "Resource")
            .Add(c => c.Destructive, "<button type='button'>Delete</button>").Add(c => c.ShowDestructiveActions, false));
        Assert.IsEmpty(cut.FindAll(".collection-actions-menu"));
        cut.Render(p => p.Add(c => c.ShowDestructiveActions, true));
        Assert.HasCount(1, cut.FindAll(".collection-actions-menu button"));
        Assert.IsEmpty(cut.FindAll("hr"));
        cut.Render(p => p.Add(c => c.ShowDestructiveActions, false));
        Assert.IsEmpty(cut.FindAll(".collection-actions-menu"));
    }

    [TestMethod]
    public async Task DisposalWaitsForPendingMenuUpdatesAndNeverUsesAReleasedModule()
    {
        using var context = new BunitContext();
        var runtime = new ControlledRuntime();
        context.Services.AddLocalization();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        RenderFragment actions = builder => builder.AddContent(0, "Open");
        var cut = context.Render<CollectionActions>(parameters => parameters
            .Add(value => value.ResourceName, "Resource")
            .Add(value => value.Secondary, actions));
        Assert.AreEqual(1, runtime.Module.Attachments);

        runtime.Module.PauseNextReleases = 2;
        cut.Render(parameters => parameters.Add(value => value.Secondary, (RenderFragment?)null));
        cut.Render(parameters => parameters.Add(value => value.Secondary, actions));
        Task? disposal = null;
        await cut.InvokeAsync(() => { disposal = cut.Instance.DisposeAsync().AsTask(); });
        try
        {
            Assert.IsFalse(runtime.Module.Disposed, "An in-flight menu update must retain its module until it completes.");
            Assert.IsFalse(disposal!.IsCompleted);
        }
        finally
        {
            runtime.Module.CompletePendingReleases();
            await disposal!;
        }

        Assert.AreEqual(1, runtime.Module.Attachments, "Disposal must prevent a queued render from attaching a replacement menu.");
        Assert.AreEqual(1, runtime.Module.Disposals);
        Assert.IsTrue(runtime.Module.Disposed);
    }

    [TestMethod]
    public async Task AModuleImportedAfterDisposalIsReleasedWithoutAttachingTheRemovedMenu()
    {
        using var context = new BunitContext();
        var runtime = new ControlledRuntime { DelayImport = true };
        context.Services.AddLocalization();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var cut = context.Render<CollectionActions>(parameters => parameters
            .Add(value => value.ResourceName, "Resource")
            .Add(value => value.Secondary, builder => builder.AddContent(0, "Open")));
        Task? disposal = null;
        await cut.InvokeAsync(() => { disposal = cut.Instance.DisposeAsync().AsTask(); });
        try { Assert.IsFalse(disposal!.IsCompleted, "Disposal must also wait for an outstanding import."); }
        finally
        {
            runtime.ImportReady.SetResult(runtime.Module);
            await disposal!;
            await cut.InvokeAsync(() => { });
        }
        Assert.AreEqual(0, runtime.Module.Attachments);
        Assert.AreEqual(1, runtime.Module.Disposals);
    }

    [TestMethod]
    public void LiveInteropFailuresRemainVisibleToTheRenderer()
    {
        using var context = new BunitContext();
        var runtime = new ControlledRuntime();
        runtime.Module.FailAttachWithDisconnect = true;
        context.Services.AddLocalization();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        Assert.ThrowsExactly<JSDisconnectedException>(() => context.Render<CollectionActions>(parameters => parameters
            .Add(value => value.ResourceName, "Resource")
            .Add(value => value.Secondary, builder => builder.AddContent(0, "Open"))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CircuitCancellationDuringMenuDisposalDoesNotEscape(bool cancelModuleDisposal)
    {
        using var context = new BunitContext();
        var runtime = new ControlledRuntime();
        runtime.Module.CancelRelease = !cancelModuleDisposal;
        runtime.Module.CancelDisposal = cancelModuleDisposal;
        context.Services.AddLocalization();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var cut = context.Render<CollectionActions>(p => p.Add(c => c.ResourceName, "Resource")
            .Add(c => c.Secondary, builder => builder.AddContent(0, "Open")));
        await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());
        await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());
        Assert.AreEqual(1, runtime.Module.Attachments);
        Assert.AreEqual(cancelModuleDisposal ? 1 : 0, runtime.Module.Disposals);
    }
    [TestMethod]
    public async Task ACancelledImportIsToleratedAfterTheMenuHasBeenDisposed()
    {
        using var context = new BunitContext();
        var runtime = new ControlledRuntime { DelayImport = true };
        context.Services.AddLocalization();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var cut = context.Render<CollectionActions>(p => p.Add(c => c.ResourceName, "Resource")
            .Add(c => c.Secondary, builder => builder.AddContent(0, "Open")));
        Task? disposal = null;
        await cut.InvokeAsync(() => { disposal = cut.Instance.DisposeAsync().AsTask(); });
        runtime.ImportReady.SetCanceled();
        await disposal!;
        await cut.InvokeAsync(() => { });
        Assert.AreEqual(0, runtime.Module.Attachments);
        Assert.AreEqual(0, runtime.Module.Disposals);
    }

    private sealed class ControlledRuntime : IJSRuntime
    {
        public ControlledModule Module { get; } = new();
        public bool DelayImport { get; init; }
        public TaskCompletionSource<IJSObjectReference> ImportReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.AreEqual("import", identifier);
            return DelayImport ? new(ImportAsync<TValue>()) : ValueTask.FromResult((TValue)(object)Module);
        }
        private async Task<TValue> ImportAsync<TValue>() => (TValue)await ImportReady.Task;
    }

    private sealed class ControlledModule : IJSObjectReference
    {
        private readonly List<TaskCompletionSource> pendingReleases = [];
        public int PauseNextReleases { get; set; }
        public int Attachments { get; private set; }
        public int Disposals { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailAttachWithDisconnect { get; set; }
        public bool CancelRelease { get; set; }
        public bool CancelDisposal { get; set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (identifier == "attachActions")
            {
                if (FailAttachWithDisconnect) throw new JSDisconnectedException("The live circuit disconnected.");
                Attachments++;
            }
            else
            {
                Assert.AreEqual("releaseActions", identifier);
                if (CancelRelease) throw new TaskCanceledException();
                if (PauseNextReleases-- > 0)
                {
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingReleases.Add(completion);
                    return new(WaitAsync<TValue>(completion.Task));
                }
            }
            return ValueTask.FromResult(default(TValue)!);
        }
        private static async Task<TValue> WaitAsync<TValue>(Task completion) { await completion; return default!; }
        public void CompletePendingReleases() { foreach (var completion in pendingReleases) completion.TrySetResult(); }
        public ValueTask DisposeAsync() { Disposals++; Disposed = true; if (CancelDisposal) throw new TaskCanceledException(); return ValueTask.CompletedTask; }
    }
}
