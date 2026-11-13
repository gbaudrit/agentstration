using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class CollectionActionsLifecycleTests
{
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
        public ValueTask DisposeAsync() { Disposals++; Disposed = true; return ValueTask.CompletedTask; }
    }
}
