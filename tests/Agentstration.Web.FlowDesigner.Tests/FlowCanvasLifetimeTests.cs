using Agentstration.Web.FlowDesigner.Components;
using Blazor.Diagrams.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
public sealed class FlowCanvasLifetimeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisposalReleasesLateImportsAndRegistrationsExactlyOnce(bool delayRegistration)
    {
        using var context = new BunitContext();
        var runtime = new ControlledRuntime(delayRegistration);
        context.Services.AddLocalization();
        context.Services.AddSingleton<IJSRuntime>(runtime);
        context.ComponentFactories.AddStub<DiagramCanvas>();
        var rendered = context.Render<FlowCanvas>(parameters => parameters.Add(value => value.Document, new([], [])));
        Assert.IsTrue(runtime.PendingReached);
        Task? disposal = null;
        await rendered.InvokeAsync(() => { disposal = rendered.Instance.DisposeAsync().AsTask(); });
        try
        {
            Assert.IsFalse(disposal!.IsCompleted);
            Assert.AreEqual(0, runtime.Module.Disposals);
        }
        finally
        {
            runtime.Complete();
            await disposal!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(delayRegistration ? 1 : 0, runtime.Module.Attachments);
        Assert.AreEqual(1, runtime.Module.Disposals);
        Assert.AreEqual(delayRegistration ? 1 : 0, runtime.Registration.Releases);
        Assert.AreEqual(delayRegistration ? 1 : 0, runtime.Registration.Disposals);
        await rendered.InvokeAsync(rendered.Instance.DisposeAsync);
        Assert.AreEqual(1, runtime.Module.Disposals);
    }

    private sealed class ControlledRuntime : IJSRuntime
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool delayRegistration;
        public ControlledModule Module { get; }
        public ControlledRegistration Registration { get; } = new();
        public bool PendingReached { get; private set; }
        public ControlledRuntime(bool delayRegistration)
        {
            this.delayRegistration = delayRegistration;
            Module = new(this);
        }
        public void Complete() => ready.SetResult();
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            Assert.AreEqual("import", identifier);
            if (delayRegistration) return ValueTask.FromResult((T)(object)Module);
            PendingReached = true;
            return new(WaitAsync<T>(Module));
        }
        private async Task<T> WaitAsync<T>(IJSObjectReference result) { await ready.Task; return (T)result; }
        public sealed class ControlledModule(ControlledRuntime runtime) : IJSObjectReference
        {
            public int Attachments { get; private set; }
            public int Disposals { get; private set; }
            public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
            public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
            {
                Assert.AreEqual("attachCtrlWheelZoom", identifier);
                Attachments++;
                runtime.PendingReached = true;
                return new(runtime.WaitAsync<T>(runtime.Registration));
            }
            public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        }
        public sealed class ControlledRegistration : IJSObjectReference
        {
            public int Releases { get; private set; }
            public int Disposals { get; private set; }
            public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
            public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
            {
                Assert.AreEqual("dispose", identifier); Releases++;
                return ValueTask.FromResult(default(T)!);
            }
            public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        }
    }
}
