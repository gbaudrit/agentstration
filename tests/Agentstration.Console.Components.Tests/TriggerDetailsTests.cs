using Agentstration.Triggers;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class TriggerDetailsTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DetailsWaitForTheSelectedContextBeforeRenderingManagementActions(bool canWrite)
    {
        using var context = new BunitContext();
        var provider = new PendingContextProvider();
        var state = new ConsoleContextState(provider);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(state);
        context.Services.AddSingleton<ITriggerApiClient>(new TriggerClient());
        context.Services.AddSingleton<NotificationState>();
        context.Services.AddSingleton(TimeProvider.System);

        // The layout starts loading permissions independently of its route component.
        var loadingContext = state.LoadAsync(default);
        var rendered = context.Render<TriggerDetails>(parameters => parameters.Add(p => p.Name, "scheduled-flow"));
        Assert.HasCount(0, rendered.FindAll("[data-testid=trigger-enable]"));

        await rendered.InvokeAsync(async () =>
        {
            provider.Complete(canWrite);
            await loadingContext;
        });

        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll("[data-testid=resource-trigger-details]"));
            Assert.HasCount(canWrite ? 1 : 0, rendered.FindAll("[data-testid=trigger-enable]"));
        });
    }

    private sealed class PendingContextProvider : IConsoleContextProvider
    {
        private readonly TaskCompletionSource<ConsoleContextSnapshot> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => completion.Task.WaitAsync(cancellationToken);
        public void Complete(bool canWrite) => completion.SetResult(new(Guid.Empty, "Administrator", "/instance", ConsoleContextKind.Instance,
            Guid.Empty, "tenant", "Tenant", Guid.Empty, "operational", "Operational", new HashSet<string>(canWrite ? ["resources/read", "resources/write"] : ["resources/read"]), [], []));
    }

    private sealed class TriggerClient : ITriggerApiClient
    {
        public Task<ResourceSnapshot<TriggerResource>?> GetAsync(ResourceNamespace resourceNamespace, string name, CancellationToken cancellationToken) => Task.FromResult<ResourceSnapshot<TriggerResource>?>(new(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = TriggerResourceKinds.Trigger,
            Metadata = new() { Name = name },
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            Definition = new()
            {
                DisplayName = "Scheduled Flow", Enabled = false,
                Source = new() { Schedule = new() { Type = TriggerScheduleType.Interval, Every = "PT1H" } },
                Target = new() { Flow = new() { Name = "source-flow", ScopeRef = ResourceScopeRef.Instance } }
            }
        }, "\"fixture\""));
        public Task<IReadOnlyList<TriggerOccurrence>> ListHistoryAsync(ResourceNamespace resourceNamespace, string name, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TriggerOccurrence>>([]);
        public Task<IReadOnlyList<TriggerResource>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<TriggerResource>> SaveAsync(TriggerResource resource, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace resourceNamespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TriggerOccurrence> RunNowAsync(ResourceNamespace resourceNamespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DateTimeOffset>> PreviewScheduleAsync(TriggerSchedule schedule, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
