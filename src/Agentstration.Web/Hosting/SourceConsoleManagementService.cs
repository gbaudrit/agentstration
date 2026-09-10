using Agentstration.Aep.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;

namespace Agentstration.Web.Hosting;

public sealed record SourceConsoleChannelView(
    SourceChannelDefinition Definition,
    SourceChannelStatusView Status,
    IReadOnlyList<SourceChannelSnapshotResource> Snapshots,
    SourceChannelSnapshotResource? CurrentSnapshot,
    SourceChannelSnapshotVerificationView? Verification,
    IReadOnlyList<SourceCatalogView> Catalogs,
    SourceConsoleCatalogFailure? CatalogFailure);

public sealed record SourceConsoleCatalogFailure(string Code, string Message);

public sealed record SourceConsoleListItem(SourceView Source, SourceVersionResource? LatestVersion);

public sealed record SourceConsoleProviderCandidate(
    string Key,
    string RegistrationName,
    ResourceNamespace RegistrationNamespace,
    ResourceScopeRef RegistrationScopeRef,
    string DisplayName,
    string ContributionId);

public sealed record SourceConsoleBindingSelection(
    string Name,
    string TargetKind,
    ResourceReference? Target,
    SourceConsoleProviderCandidate? Candidate);

public sealed record SourceConsoleDetailView(
    SourceView Source,
    IReadOnlyList<SourceVersionResource> Versions,
    SourceVersionResource SelectedVersion,
    SourceDefinitionVerificationView Verification,
    SourceBindingStatusView Bindings,
    IReadOnlyList<StoredResource<SourceProviderResource>> Providers,
    IReadOnlyList<SourceConsoleProviderCandidate> ProviderCandidates,
    IReadOnlyList<SourceConsoleChannelView> Channels);

public sealed class SourceConsoleManagementService(
    SourceManagementService sources,
    SourceBindingManagementService bindings,
    SourceProviderManagementService providers,
    ExtensionManagementService extensions,
    SourceChannelSnapshotService snapshots,
    SourceCatalogService catalogs,
    SourcePackInstallationService sourcePacks,
    SourceVerificationService verification,
    IResourceScopeResolver resourceScopes,
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
            SourceConsoleCatalogFailure? catalogFailure = null;
            if (current is not null)
            {
                snapshotVerification = await verification.VerifySnapshotAsync(selectedVersion, current, cancellationToken);
                if (status.Compatibility.Status == SourceChannelCompatibilityStatus.Compatible)
                {
                    try
                    {
                        discoveredCatalogs = await catalogs.BrowseAsync(
                            scopeRef, publisher, name, selectedVersion.Uid, channel.Name, current.Uid, locale, cancellationToken);
                    }
                    catch (SourceValidationException exception)
                    {
                        catalogFailure = new(exception.Code, exception.Message);
                    }
                }
            }
            channelViews.Add(new(channel, status, history, current, snapshotVerification, discoveredCatalogs, catalogFailure));
        }

        var availableProviders = await providers.ListVisibleAsync(scopeRef, cancellationToken);
        var providerCandidates = await DiscoverProviderCandidatesAsync(scopeRef, availableProviders, cancellationToken);
        return new(
            source,
            versions,
            selectedVersion,
            await verification.VerifyDefinitionAsync(selectedVersion, cancellationToken),
            bindingStatus,
            availableProviders,
            providerCandidates,
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

    public async Task UpdateRefreshConfigurationAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        SourceRefreshConfiguration refresh,
        string etag,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        _ = await sources.UpdateRefreshConfigurationExactAsync(
            scopeRef, publisher, name, refresh, etag, cancellationToken);
    }

    public async Task DeleteSourceAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        string etag,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        await sources.DeleteExactAsync(scopeRef, publisher, name, etag, cancellationToken);
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
        var resolvedSelections = new List<SourceBindingSelection>(selections.Count);
        foreach (var selection in selections)
        {
            var target = selection.Target
                ?? await EnsureProviderAsync(selection.Candidate
                    ?? throw new SourceProviderValidationException("A Source Provider selection is required."), cancellationToken);
            resolvedSelections.Add(new SourceBindingSelection
            {
                Name = selection.Name,
                TargetKind = selection.TargetKind,
                Target = target
            });
        }
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

    public async Task<SourcePackInstallationPreview> PreviewPackAsync(
        SourcePackSelection selection,
        IReadOnlyList<PackBindingSelection> bindings,
        bool replaceExisting,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        return await sourcePacks.PreviewAsync(
            selection, bindings, replaceExisting, new PackRemovalOptions(), cancellationToken);
    }

    public async Task<StoredResource<InstalledPackResource>> InstallPackAsync(
        SourcePackSelection selection,
        string expectedPreviewDigest,
        bool replaceExisting,
        IReadOnlyList<PackBindingSelection> bindings,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        return await sourcePacks.InstallAsync(
            selection, expectedPreviewDigest, replaceExisting, bindings, new PackRemovalOptions(), cancellationToken);
    }

    public async Task<SourceImportResult> RefreshSourceAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        return await sources.RefreshExactAsync(
            scopeRef, publisher, name, SourceRefreshTrigger.Manual, cancellationToken);
    }

    private async Task EnsurePlatformAdministratorAsync(Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        if (!await platformAuthorization.IsPlatformAdministratorAsync(actorPrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
    }

    private async Task<IReadOnlyList<SourceConsoleProviderCandidate>> DiscoverProviderCandidatesAsync(
        ResourceScopeRef sourceScopeRef,
        IReadOnlyList<StoredResource<SourceProviderResource>> availableProviders,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SourceConsoleProviderCandidate>();
        var sourceScope = await resourceScopes.ResolveAsync(sourceScopeRef, cancellationToken)
            ?? throw new SourceProviderValidationException($"Source scope '{sourceScopeRef}' does not exist.");
        var visibleScopeRefs = new HashSet<ResourceScopeRef>(
            [sourceScope.Scope.Ref, .. sourceScope.Ancestors.Select(value => value.Ref)]);
        foreach (var extension in await extensions.ListAsync(cancellationToken))
        {
            if (!string.Equals(extension.Status, "available", StringComparison.OrdinalIgnoreCase)
                || extension.RegistrationScopeRef is not { } registrationScopeRef
                || !visibleScopeRefs.Contains(registrationScopeRef)) continue;
            foreach (var contribution in extension.Contributions.Where(value =>
                string.Equals(value.Kind, AepContributionKinds.SourceProvider, StringComparison.OrdinalIgnoreCase)))
            {
                if (availableProviders.Any(provider => References(provider.Value, extension, contribution.Id))) continue;
                candidates.Add(new(
                    $"{registrationScopeRef.Value}\n{extension.RegistrationNamespace}\n{extension.RegistrationName}\n{contribution.Id}",
                    extension.RegistrationName,
                    new ResourceNamespace(extension.RegistrationNamespace),
                    registrationScopeRef,
                    extension.Extension?.Name ?? extension.RegistrationName,
                    contribution.Id));
            }
        }
        return candidates;
    }

    private async Task<ResourceReference> EnsureProviderAsync(
        SourceConsoleProviderCandidate candidate,
        CancellationToken cancellationToken)
    {
        var extension = (await extensions.ListAsync(cancellationToken)).SingleOrDefault(value =>
            string.Equals(value.RegistrationNamespace, candidate.RegistrationNamespace.Value, StringComparison.Ordinal)
            && string.Equals(value.RegistrationName, candidate.RegistrationName, StringComparison.Ordinal)
            && value.RegistrationScopeRef == candidate.RegistrationScopeRef);
        if (extension is null
            || !string.Equals(extension.Status, "available", StringComparison.OrdinalIgnoreCase)
            || !extension.Contributions.Any(value =>
                string.Equals(value.Kind, AepContributionKinds.SourceProvider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value.Id, candidate.ContributionId, StringComparison.OrdinalIgnoreCase)))
            throw new SourceProviderValidationException("The selected Source Provider contribution is no longer available.");

        var availableProviders = await providers.ListAsync(cancellationToken);
        var existing = availableProviders.SingleOrDefault(provider => References(provider.Value, extension, candidate.ContributionId));
        if (existing is not null) return ProviderReference(existing.Value);

        var baseName = Slug($"{candidate.RegistrationName}-{candidate.ContributionId}");
        var name = baseName;
        for (var suffix = 2; availableProviders.Any(value =>
                 value.Value.ScopeRef == candidate.RegistrationScopeRef
                 && value.Value.Namespace == candidate.RegistrationNamespace
                 && string.Equals(value.Value.Name, name, StringComparison.Ordinal)); suffix++)
            name = $"{baseName}-{suffix}";

        var resource = new SourceProviderResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.SourceProvider,
            Metadata = new ResourceMetadata { Namespace = candidate.RegistrationNamespace, Name = name },
            ScopeRef = candidate.RegistrationScopeRef,
            Definition = new SourceProviderProperties
            {
                DisplayName = candidate.DisplayName,
                Extension = new(candidate.RegistrationName, candidate.RegistrationScopeRef, candidate.RegistrationNamespace),
                ContributionId = candidate.ContributionId
            }
        };
        StoredResource<SourceProviderResource> created;
        try
        {
            created = await providers.CreateAsync(resource, cancellationToken);
        }
        catch (ControlPlaneConcurrencyException)
        {
            var raced = (await providers.ListAsync(cancellationToken))
                .SingleOrDefault(provider => References(provider.Value, extension, candidate.ContributionId));
            if (raced is null) throw;
            created = raced;
        }
        return ProviderReference(created.Value);
    }

    private static bool References(SourceProviderResource provider, ExtensionView extension, string contributionId)
    {
        var address = provider.Definition.Extension.Resolve(provider.Namespace, ResourceKinds.ExtensionRegistration);
        return address.Namespace.Value == extension.RegistrationNamespace
            && string.Equals(address.Name, extension.RegistrationName, StringComparison.Ordinal)
            && provider.Definition.Extension.ScopeRef == extension.RegistrationScopeRef
            && string.Equals(provider.Definition.ContributionId, contributionId, StringComparison.OrdinalIgnoreCase);
    }

    private static ResourceReference ProviderReference(SourceProviderResource provider) =>
        new(
            provider.Name,
            provider.ScopeRef ?? throw new SourceProviderValidationException("The Source Provider has no ownership scope."),
            provider.Namespace);

    private static string Slug(string value)
    {
        var slug = string.Concat(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) slug = "source-provider";
        return slug.Length <= 128 ? slug : slug[..128].TrimEnd('-');
    }
}
