using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Triggers;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class TriggerDetailsPermissionsTests
{
    private static readonly Guid ResourceWorkspace = Guid.Parse("11111111-1111-1111-1111-111111111111");
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DelayedSharedPermissionsDoNotLeaveTriggerCommandsMissingOrUnauthorized(bool canManage)
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        var snapshot = new TaskCompletionSource<ConsoleContextSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DeferredContextProvider(snapshot.Task);
        var state = new ConsoleContextState(provider);
        context.Services.AddSingleton(state);
        var client = new TriggerClient();
        context.Services.AddSingleton<ITriggerApiClient>(client);

        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("http://localhost/triggers/permission-race");
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Setup<string>("currentLocation", _ => true).SetResult(navigation.Uri);
        module.Setup<string>("navigate", _ => true).SetResult(navigation.Uri + "#overview");

        // MainLayout starts this shared request independently of the page's resource request.
        var permissions = state.LoadAsync(default);
        var page = context.Render<TriggerDetails>(parameters => parameters.Add(value => value.Name, "permission-race"));
        Assert.IsEmpty(page.FindAll("[data-testid='trigger-enable']"));
        snapshot.SetResult(new(Guid.NewGuid(), "Reader", ResourceScopeRef.Instance.Value, ConsoleContextKind.Instance,
            Guid.NewGuid(), "tenant", "Tenant", ResourceWorkspace, "default", "Default workspace",
            canManage ? new HashSet<string> { AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite } : [AuthorizationPermissions.ResourcesRead], [], []));
        await permissions;
        page.WaitForAssertion(() =>
        {
            StringAssert.Contains(page.Markup, "Permission race Trigger");
            Assert.AreEqual(!canManage, page.Find("[data-testid='trigger-enable']").HasAttribute("disabled"));
        });
        if (canManage)
        {
            page.Find("[data-testid='trigger-enable']").Change(true);
            Assert.HasCount(1, page.FindAll("[data-testid='trigger-enable-dialog']"));
        }
        Assert.AreEqual(0, client.SaveCalls, "Opening a confirmation must not enable the Trigger.");
        Assert.AreEqual(1, provider.Calls, "The page and layout must share one permission request.");
    }

    private sealed class DeferredContextProvider(Task<ConsoleContextSnapshot> snapshot) : IConsoleContextProvider
    {
        public int Calls { get; private set; }
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) { Calls++; return snapshot.WaitAsync(cancellationToken); }
    }

    private sealed class TriggerClient : ITriggerApiClient
    {
        private readonly TriggerResource resource = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = TriggerResourceKinds.Trigger,
            Metadata = new() { Name = "permission-race" },
            ScopeRef = ResourceScopeRef.Workspace(ResourceWorkspace),
            Uid = Guid.NewGuid(),
            Definition = new()
            {
                DisplayName = "Permission race Trigger",
                Enabled = false,
                Source = new() { Schedule = new() { Type = TriggerScheduleType.Interval, Every = "PT1H" } },
                Target = new() { Flow = new() { Name = "flow", ScopeRef = ResourceScopeRef.Instance } }
            }
        };
        public int SaveCalls { get; private set; }
        public Task<ResourceSnapshot<TriggerResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => Task.FromResult<ResourceSnapshot<TriggerResource>?>(new(resource, "\"etag-1\""));
        public Task<IReadOnlyList<TriggerOccurrence>> ListHistoryAsync(ResourceNamespace @namespace, string name, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TriggerOccurrence>>([]);
        public Task<ResourceSnapshot<TriggerResource>> SaveAsync(TriggerResource value, string? etag, CancellationToken cancellationToken) { SaveCalls++; return Task.FromResult(new ResourceSnapshot<TriggerResource>(value, "\"etag-2\"")); }
        public Task<IReadOnlyList<TriggerResource>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TriggerOccurrence> RunNowAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DateTimeOffset>> PreviewScheduleAsync(TriggerSchedule schedule, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
