using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class CollectionViewLifecycleTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationDuringCollectionDisposalDoesNotBreakTheCircuit(bool cancelModuleDisposal)
    {
        using var context = new BunitContext();
        context.Services.AddLocalization();
        var runtime = new ControlledRuntime();
        runtime.Module.CancelRelease = !cancelModuleDisposal;
        runtime.Module.CancelDisposal = cancelModuleDisposal;
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var cut = Render(context);
        await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());
        Assert.AreEqual(1, runtime.Module.Attachments);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisposalWaitsForAnOutstandingImportAndNeverAttachesARemovedCollection(bool cancelImport)
    {
        using var context = new BunitContext();
        context.Services.AddLocalization();
        var runtime = new ControlledRuntime { DelayImport = true };
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var cut = Render(context);
        Task? disposal = null;
        await cut.InvokeAsync(() => { disposal = cut.Instance.DisposeAsync().AsTask(); });
        Assert.IsFalse(disposal!.IsCompleted);
        if (cancelImport) runtime.ImportReady.SetCanceled();
        else runtime.ImportReady.SetResult(runtime.Module);
        await disposal;
        await cut.InvokeAsync(() => { });
        Assert.AreEqual(0, runtime.Module.Attachments);
        Assert.AreEqual(cancelImport ? 0 : 1, runtime.Module.Disposals);
    }

    [TestMethod]
    public void LiveInteropFailuresRemainVisible()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization();
        var runtime = new ControlledRuntime();
        runtime.Module.FailAttachWithDisconnect = true;
        context.Services.AddSingleton<IJSRuntime>(runtime);
        Assert.ThrowsExactly<JSDisconnectedException>(() => Render(context));
    }

    private static IRenderedComponent<CollectionView<string>> Render(BunitContext context) => context.Render<CollectionView<string>>(p => p
        .Add(c => c.Items, new[] { "Saved identity" }).Add(c => c.Identity, value => builder => builder.AddContent(0, value))
        .Add(c => c.InformationLabels, Array.Empty<string>()).Add(c => c.Information, _ => _ => { }));

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
            if (identifier == "observeCollection")
            {
                if (FailAttachWithDisconnect) throw new JSDisconnectedException("The live circuit disconnected.");
                Attachments++;
            }
            else
            {
                Assert.AreEqual("releaseCollection", identifier);
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
