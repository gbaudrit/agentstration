using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;

namespace Agentstration.Web.Hosting;

public sealed record SourceConsoleChannelView(
    SourceChannelDefinition Definition,
    SourceChannelStatusView Status,
    IReadOnlyList<SourceChannelSnapshotResource> Snapshots,
    SourceChannelSnapshotVerificationView? Verification,
    IReadOnlyList<SourceCatalogView> Catalogs);

public sealed record SourceConsoleListItem(SourceView Source, SourceVersionResource? LatestVersion);

public sealed record SourceConsoleBindingSelection(
    string Name,
    string TargetKind,
    ResourceReference? Target);

public sealed record SourceConsoleDetailView(
    SourceView Source,
    IReadOnlyList<SourceVersionResource> Versions,
    SourceVersionResource SelectedVersion,
    SourceDefinitionVerificationView Verification,
    SourceBindingStatusView Bindings,
    IReadOnlyList<StoredResource<SourceProviderResource>> Providers,
    IReadOnlyList<SourceConsoleChannelView> Channels);

public sealed class SourceConsoleManagementService(
    SourceManagementService sources,
    SourceBindingManagementService bindings,
    SourceProviderManagementService providers,
    SourceChannelSnapshotService snapshots,
    SourceCatalogService catalogs,
    SourceVerificationService verification,
    IPlatformAuthorizationService platformAuthorization)
{
    public async Task<IReadOnlyList<SourceConsoleListItem>> ListAsync(Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        var available = await sources.ListAsync(cancellationToken);
        var results = new List<SourceConsoleListItem>(available.Count);
        foreach (var source in available)
        {
            var scopeRef = source.Source.ScopeRef
                ?? throw new InvalidOperationException($"Source '{source.Source.Address}' has no ownership scope.");
            var versions = await sources.ListVersionsExactAsync(
                scopeRef, source.Source.Definition.Publisher, source.Source.Name, cancellationToken);
            results.Add(new(source, versions.FirstOrDefault()));
        }
        return results;
    }

    public async Task<SourceImportResult> ImportYamlAsync(
        string manifest,
        ResourceScopeRef scopeRef,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        return await sources.ImportYamlAsync(manifest, scopeRef, cancellationToken);
    }

    public async Task<SourceImportResult> ImportUrlAsync(
        Uri url,
        ResourceScopeRef scopeRef,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        return await sources.ImportUrlAsync(url, scopeRef, cancellationToken);
    }

    public async Task<SourceConsoleDetailView> GetAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid? versionUid,
        string? locale,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        var source = await sources.GetExactAsync(scopeRef, publisher, name, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new ResourceNamespace(publisher)));
        var versions = await sources.ListVersionsExactAsync(scopeRef, publisher, name, cancellationToken);
        var selectedVersion = versionUid is null
            ? versions.FirstOrDefault()
            : versions.SingleOrDefault(value => value.Uid == versionUid);
        if (selectedVersion is null)
            throw new ControlPlaneResourceNotFoundException(
                new(ResourceKinds.SourceVersion, versionUid?.ToString("D") ?? name));

        var bindingStatus = await bindings.GetStatusExactAsync(
            scopeRef, publisher, name, selectedVersion.Uid, cancellationToken);
        var channelViews = new List<SourceConsoleChannelView>();
        foreach (var channel in selectedVersion.Definition.PublishedDefinition.Channels)
        {
            var status = await snapshots.GetStatusAsync(
                scopeRef, publisher, name, selectedVersion.Uid, channel.Name, cancellationToken);
            var history = await snapshots.ListAsync(
                scopeRef, publisher, name, selectedVersion.Uid, channel.Name, cancellationToken);
            var current = status.Refresh?.Definition.CurrentSnapshotUid is Guid currentUid
                ? history.SingleOrDefault(value => value.Uid == currentUid)
                : null;
            SourceChannelSnapshotVerificationView? snapshotVerification = null;
            IReadOnlyList<SourceCatalogView> discoveredCatalogs = [];
            if (current is not null)
            {
                snapshotVerification = await verification.VerifySnapshotAsync(selectedVersion, current, cancellationToken);
                if (status.Compatibility.Status == SourceChannelCompatibilityStatus.Compatible)
                {
                    discoveredCatalogs = await catalogs.BrowseAsync(
                        scopeRef, publisher, name, selectedVersion.Uid, channel.Name, current.Uid, locale, cancellationToken);
                }
            }
            channelViews.Add(new(channel, status, history, snapshotVerification, discoveredCatalogs));
        }

        var availableProviders = await providers.ListAsync(cancellationToken);
        return new(
            source,
            versions,
            selectedVersion,
            await verification.VerifyDefinitionAsync(selectedVersion, cancellationToken),
            bindingStatus,
            availableProviders,
            channelViews);
    }

    public async Task UpdateDisplayNameAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        string displayName,
        string etag,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        _ = await sources.UpdateDisplayNameExactAsync(
            scopeRef, publisher, name, displayName, etag, cancellationToken);
    }

    public async Task ConfigureBindingsAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        IReadOnlyList<SourceConsoleBindingSelection> selections,
        string etag,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        var resolvedSelections = selections.Select(selection => new SourceBindingSelection
        {
            Name = selection.Name,
            TargetKind = selection.TargetKind,
            Target = selection.Target ?? throw new SourceProviderValidationException("A configured Source Provider selection is required.")
        }).ToArray();
        _ = await bindings.ConfigureExactAsync(
            scopeRef, publisher, name, versionUid, resolvedSelections, etag, cancellationToken);
    }

    public async Task RefreshChannelAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        string channel,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        _ = await snapshots.RefreshExactAsync(
            scopeRef, publisher, name, versionUid, channel, cancellationToken);
    }

    public async Task<SourceImportResult> RefreshSourceAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        var source = await sources.GetExactAsync(scopeRef, publisher, name, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Source, name, new ResourceNamespace(publisher)));
        var origin = source.Configuration.Definition.Origin?.Url;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var url))
            throw new SourceValidationException("source_origin_missing", "A pasted-only Source has no HTTP(S) origin to refresh.");
        return await sources.ImportUrlAsync(url, scopeRef, cancellationToken);
    }

    private async Task EnsurePlatformAdministratorAsync(Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        if (!await platformAuthorization.IsPlatformAdministratorAsync(actorPrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
    }

}
