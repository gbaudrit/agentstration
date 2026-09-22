using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Console.Components.Tests;

[TestClass]
public sealed class ConsoleEntryCommandFallbackProviderTests
{
    private static readonly Guid OwnerWorkspaceId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [TestMethod]
    public async Task ResolvesTheSingleExecutablePrimaryEntryAndPreservesTheExactQuery()
    {
        const string query = "  comment configurer ollama ? & plus  ";
        var provider = CreateProvider(Entry("assistant", EntryConsoleRole.Primary, canInvoke: true));

        var result = await provider.ResolveAsync(query, default);

        Assert.IsNotNull(result);
        Assert.AreEqual("Assistant", result.Label);
        Assert.IsTrue(result.Url.StartsWith($"/entry-interactions/{OwnerWorkspaceId:D}/tools/assistant?query=", StringComparison.Ordinal));
        var encoded = result.Url[(result.Url.IndexOf("?query=", StringComparison.Ordinal) + 7)..];
        Assert.AreEqual(query, Uri.UnescapeDataString(encoded));
    }

    [TestMethod]
    public async Task FailsClosedWithoutOneEffectivePrimaryEntry()
    {
        var cases = new[]
        {
            Array.Empty<EntryResponse>(),
            new[] { Entry("standard", EntryConsoleRole.Standard, canInvoke: true) },
            new[] { Entry("disabled", EntryConsoleRole.Primary, canInvoke: false) },
            new[] { Entry("first", EntryConsoleRole.Primary, canInvoke: true), Entry("second", EntryConsoleRole.Primary, canInvoke: true) }
        };

        foreach (var entries in cases)
            Assert.IsNull(await CreateProvider(entries).ResolveAsync("unmatched query", default));
    }

    [TestMethod]
    public async Task DiscoveryFailureDegradesToNoFallback()
    {
        var provider = new ConsoleEntryCommandFallbackProvider(
            new StubEntryClient([], new InvalidOperationException("unauthorized")),
            NullLogger<ConsoleEntryCommandFallbackProvider>.Instance);

        Assert.IsNull(await provider.ResolveAsync("unmatched query", default));
    }

    private static ConsoleEntryCommandFallbackProvider CreateProvider(params EntryResponse[] entries) =>
        new(new StubEntryClient(entries), NullLogger<ConsoleEntryCommandFallbackProvider>.Instance);

    private static EntryResponse Entry(string name, EntryConsoleRole role, bool canInvoke) => new(
        OwnerWorkspaceId,
        name,
        name,
        WorkResourceTypes.Entries,
        WorkplaceApiVersions.CoreV1,
        name == "assistant" ? "Assistant" : name,
        null,
        new EntryPresentation(),
        new EntryExposure
        {
            Surfaces = [EntryExposureSurface.Console],
            WorkplacePlacements = [],
            Console = new(role)
        },
        new EntryResolvedTarget("flow", "1.0.0"),
        new EntryBehavior(),
        1,
        DateTimeOffset.UnixEpoch)
    {
        Namespace = new ResourceNamespace("tools"),
        Execution = new(canInvoke, canInvoke ? EntryExecutionAvailability.Executable : EntryExecutionAvailability.Disabled, canInvoke ? null : "entry_flow_disabled")
    };

    private sealed class StubEntryClient(
        IReadOnlyList<EntryResponse> entries,
        Exception? error = null) : IEntryAdministrationApiClient
    {
        public Task<IReadOnlyList<EntryResponse>> GetExposedEntriesAsync(EntryExposureSurface surface, EntryWorkplacePlacement? placement, CancellationToken cancellationToken) =>
            error is null
                ? Task.FromResult(entries)
                : Task.FromException<IReadOnlyList<EntryResponse>>(error);
        public Task<IReadOnlyList<EntryDraftResponse>> GetEntriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntryDraftResponse> GetEntryAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntryDraft> SaveEntryAsync(EntryDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntryValidationResponse> ValidateEntryAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntryResource> PublishEntryAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntryDependencyResponse>> GetDependenciesAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourcePickerItem>> GetResourcesAsync(EntryBindingKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntryResponse>> GetPublishedEntriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkplaceWorkspaceResponse>> GetWorkspacesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkplaceDashboardDraftResponse>> GetDashboardsAsync(string workspaceName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkplaceDashboardDraftResponse> GetDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkplaceDashboardDraft> SaveDashboardAsync(WorkplaceDashboardDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkplaceDashboard> PublishDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteDashboardAsync(string workspaceName, string dashboardName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
