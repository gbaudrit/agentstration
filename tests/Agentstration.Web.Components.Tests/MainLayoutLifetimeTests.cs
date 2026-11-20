using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class MainLayoutLifetimeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisposalWaitsForInitializationAndPreventsLateRegistrations(bool delayImport)
    {
        using var context = new BunitContext();
        var runtime = new ControlledRuntime(delayImport);
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var rendered = context.Render<MainLayout>();
        Assert.IsTrue(runtime.PendingReached);
        Task? disposal = null;
        await rendered.InvokeAsync(() => { disposal = rendered.Instance.DisposeAsync().AsTask(); });
        try
        {
            Assert.IsFalse(disposal!.IsCompleted);
            Assert.IsFalse(runtime.Modules.Values.Any(module => module.Disposed));
        }
        finally
        {
            runtime.Complete();
            await disposal!.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(runtime.Modules.Values.All(module => module.Disposed));
        Assert.IsTrue(runtime.Modules.Values.All(module => module.Registrations == 0));
        await rendered.InvokeAsync(rendered.Instance.DisposeAsync);
        Assert.IsTrue(runtime.Modules.Values.All(module => module.Disposals == 1));
    }

    [TestMethod]
    public async Task CanceledJavascriptDisposalDoesNotFailTheContainingCircuit()
    {
        using var context = new BunitContext();
        var runtime = new ControlledRuntime(false);
        runtime.Complete();
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var rendered = context.Render<MainLayout>();
        runtime.CancelDisposal = true;
        await rendered.InvokeAsync(rendered.Instance.DisposeAsync);
        await rendered.InvokeAsync(rendered.Instance.DisposeAsync);
        Assert.AreEqual(1, runtime.Modules.Values.Sum(module => module.Disposals));
    }

    private sealed class ControlledRuntime(bool delayImport) : IJSRuntime
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, ControlledModule> Modules { get; } = [];
        public bool PendingReached { get; private set; }
        public bool CancelDisposal { get; set; }
        public void Complete() => ready.SetResult();
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            Assert.AreEqual("import", identifier);
            var path = (string)args![0]!;
            var module = new ControlledModule(this);
            Modules.Add(path, module);
            if (delayImport && path.EndsWith("/theme-preferences.js", StringComparison.Ordinal))
            {
                PendingReached = true;
                return new(CompleteImportAsync<T>(module));
            }
            return ValueTask.FromResult((T)(object)module);
        }
        private async Task<T> CompleteImportAsync<T>(ControlledModule module)
        {
            await ready.Task;
            return (T)(object)module;
        }
        public sealed class ControlledModule(ControlledRuntime runtime) : IJSObjectReference
        {
            public bool Disposed { get; private set; }
            public int Disposals { get; private set; }
            public int Registrations { get; private set; }
            public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
            public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                if (identifier == "prefersDarkTheme")
                {
                    runtime.PendingReached = true;
                    return new(ReadThemeAsync<T>());
                }
                if (identifier.StartsWith("register", StringComparison.Ordinal)) Registrations++;
                return ValueTask.FromResult(default(T)!);
            }
            private async Task<T> ReadThemeAsync<T>() { await runtime.ready.Task; return (T)(object)false; }
            public ValueTask DisposeAsync()
            {
                Disposals++; Disposed = true;
                return runtime.CancelDisposal ? ValueTask.FromException(new TaskCanceledException()) : ValueTask.CompletedTask;
            }
        }
    }
}
