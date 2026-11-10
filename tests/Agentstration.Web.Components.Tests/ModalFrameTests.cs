using Bunit;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ModalFrameTests
{
    [TestMethod]
    public async Task AsyncChildRendersKeepOneRegistrationAndReleaseItsFocusHandleOnce()
    {
        using var context = new BunitContext();
        var runtime = new DelayedRuntime();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var registration = new DialogRegistration();
        var cut = context.Render<ModalFrame>(parameters => parameters
            .Add(value => value.IsOpen, true).Add(value => value.TitleId, "title"));
        cut.Render(parameters => parameters.Add(value => value.Busy, true));
        try { Assert.AreEqual(1, runtime.Module.Opens); }
        finally { runtime.Module.Ready.SetResult(registration); }
        await cut.InvokeAsync(() => {});
        cut.Render(parameters => parameters.Add(value => value.IsOpen, false));
        cut.WaitForAssertion(() => Assert.AreEqual(1, registration.Releases));
        Assert.AreEqual(1, registration.Disposals);
    }

    private sealed class DelayedRuntime : IJSRuntime
    {
        public DelayedModule Module { get; } = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.AreEqual("import", identifier);
            return ValueTask.FromResult((TValue)(object)Module);
        }
    }

    private sealed class DelayedModule : IJSObjectReference
    {
        public TaskCompletionSource<IJSObjectReference> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Opens { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.AreEqual("openDialog", identifier);
            Opens++;
            return new(WaitAsync<TValue>());
        }
        private async Task<TValue> WaitAsync<TValue>() => (TValue)await Ready.Task;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DialogRegistration : IJSObjectReference
    {
        public int Releases { get; private set; }
        public int Disposals { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.AreEqual("dispose", identifier);
            Releases++;
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
