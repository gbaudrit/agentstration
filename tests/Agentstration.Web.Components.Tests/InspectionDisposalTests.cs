using Agentstration.Resources;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class InspectionDisposalTests
{
    [TestMethod]
    [DataRow("navigation", false)]
    [DataRow("navigation", true)]
    [DataRow("preview", false)]
    [DataRow("preview", true)]
    [DataRow("modal", false)]
    [DataRow("modal", true)]
    public async Task CanceledCleanupCannotFaultTheCircuit(string component, bool cancelModuleDisposal)
    {
        using var context = new BunitContext();
        context.Services.AddLocalization();
        var runtime = new Runtime("http://localhost/resource#overview", cancelModuleDisposal);
        context.Services.AddSingleton<IJSRuntime>(runtime);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resource#overview");
        IAsyncDisposable target = component switch
        {
            "navigation" => context.Render<ResourceNavigation>(p => p.Add(c => c.Items, new[] { new ResourceNavigationItem("overview", "Overview", "info-circle") })
                .Add(c => c.DefaultPath, "overview")).Instance,
            "preview" => context.Render<ResourceCollectionPreview>(p => p.Add(c => c.ResourceKind, "Agent")
                .Add(c => c.ChildContent, _ => _ => { })).Instance,
            _ => context.Render<ModalFrame>(p => p.Add(c => c.IsOpen, true).Add(c => c.TitleId, "inspection")).Instance
        };
        await target.DisposeAsync();
        Assert.AreEqual(1, runtime.Module.CleanupCalls);
    }

    private sealed class Runtime(string location, bool cancelModuleDisposal) : IJSRuntime
    {
        public Module Module { get; } = new(location, cancelModuleDisposal);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.AreEqual("import", identifier);
            return ValueTask.FromResult((TValue)(object)Module);
        }
    }

    private sealed class Module(string location, bool cancelModuleDisposal) : IJSObjectReference
    {
        public int CleanupCalls { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "dispose")
            {
                CleanupCalls++;
                if (!cancelModuleDisposal) throw new TaskCanceledException();
            }
            object? value = typeof(TValue) == typeof(IJSObjectReference) ? this : typeof(TValue) == typeof(string) ? location : default(TValue);
            return ValueTask.FromResult((TValue)value!);
        }
        public ValueTask DisposeAsync()
        {
            if (cancelModuleDisposal) throw new TaskCanceledException();
            return ValueTask.CompletedTask;
        }
    }
}
