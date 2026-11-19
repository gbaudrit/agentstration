using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agentstration.Web.Hosting;

public sealed class BootstrapStartupCoordinator(
    IConfiguration configuration,
    BootstrapProfileCatalog catalog,
    DeclarativeBootstrapService bootstrap,
    ExtensionEnrollmentPolicyService policies,
    AepAutomaticEnrollmentService automaticEnrollment,
    AepEnrollmentSettingsService enrollmentSettings,
    IBootstrapExtensionReferenceResolver references,
    IIdentityStore identities,
    IRequestContextScopeFactory scopes,
    IOperationalTopologyProvisioner operationalTopology,
    IWorkspacePlatformResourceProvisioner platformResources,
    TimeProvider timeProvider,
    BootstrapStartupState startup,
    ILogger<BootstrapStartupCoordinator> logger)
{
    private sealed record InitialApplication(
        IReadOnlyList<string> Profiles, string TargetLevel, IReadOnlyList<LoadedBootstrapProfile> Snapshot);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var options = configuration.GetSection("Agentstration:Bootstrap").Get<DeclarativeBootstrapOptions>() ?? new();
        if (!options.InitialBootstrapEnabled)
        {
            await automaticEnrollment.ActivateAsync(cancellationToken);
            return;
        }
        if (options.ExtensionDependencyTimeout < TimeSpan.FromSeconds(1)
            || options.ExtensionDependencyTimeout > TimeSpan.FromMinutes(30))
            throw new DeclarativeBootstrapException("Bootstrap ExtensionDependencyTimeout must be between one second and thirty minutes.");
        if (options.InitialApplications.Count > 16)
            throw new DeclarativeBootstrapException("Bootstrap supports at most sixteen additional initial applications.");
        var applications = new List<InitialApplication>();
        if (options.InitialProfiles.Count > 0)
            applications.Add(await PrepareAsync(options.InitialProfiles, "/instance", cancellationToken));
        foreach (var application in options.InitialApplications)
            applications.Add(await PrepareAsync(application.Profiles, application.TargetLevel, cancellationToken));

        foreach (var application in applications.Where(value => value.Snapshot[0].Summary.Scope == BootstrapProfileScope.Instance))
            await ApplyPhaseAsync(new(application.Profiles), BootstrapStartupPhase.Topology, application.Snapshot, cancellationToken);
        await operationalTopology.EnsureAllAsync(cancellationToken);
        await platformResources.EnsureAllAsync(cancellationToken);
        foreach (var application in applications.Where(value => value.Snapshot[0].Summary.Scope == BootstrapProfileScope.Instance))
            await ApplyPhaseAsync(new(application.Profiles), BootstrapStartupPhase.Policies, application.Snapshot, cancellationToken);
        await automaticEnrollment.ActivateAsync(cancellationToken);

        startup.EnableAdministration();
        var selections = new List<BootstrapProfileSelection>();
        foreach (var application in applications)
            selections.Add(await SelectAsync(application, cancellationToken));
        var requirements = new List<BootstrapExtensionRequirement>();
        foreach (var selection in selections)
            requirements.AddRange(await bootstrap.GetExtensionRequirementsAsync(selection, cancellationToken));
        await WaitForContributionsAsync(requirements, options.ExtensionDependencyTimeout, cancellationToken);

        // Re-read the bounded catalog before dependent resource mutation. Never apply
        // a different file set merely because the extension took time to arrive.
        foreach (var application in applications)
        {
            var current = await catalog.LoadAsync(application.Profiles, cancellationToken);
            if (!current.Select(value => value.Summary.Digest)
                .SequenceEqual(application.Snapshot.Select(value => value.Summary.Digest), StringComparer.Ordinal))
                throw new DeclarativeBootstrapException("The Bootstrap catalog changed while waiting for extension contributions.");
        }
        for (var index = 0; index < selections.Count; index++)
        {
            var selection = selections[index];
            var context = await ResolveApplicationContextAsync(selection, cancellationToken);
            using var operation = context is null ? scopes.PushSystem()
                : selection.Target!.WorkspaceId is not null ? scopes.Push(context)
                : scopes.PushTenant(context.PrincipalId, context.TenantId);
            await ApplyPhaseAsync(selection, BootstrapStartupPhase.Resources, applications[index].Snapshot, cancellationToken);
        }
    }

    private async Task<InitialApplication> PrepareAsync(
        IReadOnlyList<string> profiles, string level, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(level))
            throw new DeclarativeBootstrapException("An initial application requires an explicit TargetLevel.");
        ExtensionEnrollmentPolicyService.ValidateLevel(level);
        var loaded = await catalog.LoadAsync(profiles, cancellationToken);
        bootstrap.ValidateStartupProfiles(loaded);
        var expected = level == "/instance" ? BootstrapProfileScope.Instance
            : level.Split('/').Length == 3 ? BootstrapProfileScope.Tenant : BootstrapProfileScope.Workspace;
        if (loaded[0].Summary.Scope != expected)
            throw new DeclarativeBootstrapException($"Initial application target '{level}' does not match its profile scope.");
        return new(profiles, level, loaded);
    }

    private async Task<BootstrapProfileSelection> SelectAsync(InitialApplication application, CancellationToken cancellationToken)
    {
        var target = await policies.ResolveLevelAsync(application.TargetLevel, "initial-application", cancellationToken);
        return new(application.Profiles, target.ScopeRef.Kind switch
        {
            ResourceScopeKind.Instance => null,
            ResourceScopeKind.Tenant => new(target.TenantId),
            ResourceScopeKind.Workspace => new(target.TenantId, target.ScopeRef.TargetId),
            _ => throw new DeclarativeBootstrapException("Unsupported initial application target.")
        });
    }

    private async Task<RequestContext?> ResolveApplicationContextAsync(BootstrapProfileSelection selection, CancellationToken cancellationToken)
    {
        if (selection.Target is null) return null;
        var administrators = await identities.ListPlatformAdministratorsAsync(cancellationToken);
        var administrator = administrators.OrderBy(value => value.PrincipalId).FirstOrDefault()
            ?? throw new DeclarativeBootstrapException("Scoped initial applications require a Platform administrator created by topology Bootstrap or already present in the instance.");
        return new(administrator.PrincipalId, selection.Target.TenantId!.Value, selection.Target.WorkspaceId ?? Guid.Empty);
    }

    private async Task ApplyPhaseAsync(BootstrapProfileSelection selection, BootstrapStartupPhase phase,
        IReadOnlyList<LoadedBootstrapProfile> expected, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Applying Bootstrap phase {Phase} for profiles {Profiles}.", phase, string.Join(",", selection.Profiles));
        var result = await bootstrap.ExecutePhaseAsync(selection, phase, cancellationToken, expected.Select(profile => profile.Summary.Digest).ToArray());
        if (result.Error is not null) throw new DeclarativeBootstrapException(result.Error);
    }

    private async Task WaitForContributionsAsync(
        IReadOnlyList<BootstrapExtensionRequirement> requirements, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (requirements.Count == 0) return;
        var requiredIds = requirements.Select(value => value.Selector.ExtensionId).ToHashSet(StringComparer.Ordinal);
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var lastDiagnostic = string.Join("; ", requirements.Select(requirement =>
            $"{requirement.Location}: {requirement.ResourceKind}/{requirement.ResourceName} requires {requirement.Selector.ExtensionId}/{requirement.Selector.Kind}/{requirement.Selector.ContributionId}"));
        try
        {
            while (true)
            {
                wait.Token.ThrowIfCancellationRequested();
                await automaticEnrollment.ProcessPendingAsync(wait.Token, requiredIds);
                var missing = false;
                foreach (var requirement in requirements)
                {
                    try
                    {
                        if (requirement.Selector.Kind == ExtensionKinds.ExtensionRegistration)
                            _ = await references.ResolveExtensionAsync(new()
                            {
                                Id = requirement.Selector.ExtensionId,
                                InstanceId = requirement.Selector.InstanceId
                            }, requirement.ConsumerScope, wait.Token);
                        else
                            _ = await references.ResolveContributionAsync(requirement.Selector, requirement.ConsumerScope, wait.Token);
                    }
                    catch (BootstrapDependencyException exception)
                    {
                        lastDiagnostic = $"{requirement.Profile}/{requirement.Location}: {requirement.ResourceKind}/{requirement.ResourceName} requires extension '{requirement.Selector.ExtensionId}' installation '{requirement.Selector.InstanceId}', {requirement.Selector.Kind}/{requirement.Selector.ContributionId} [{exception.Code}]: {exception.Message}";
                        if (exception.Code is not ("extension_unavailable" or "contribution_unavailable"))
                            throw new DeclarativeBootstrapException(lastDiagnostic, exception);
                        if (!await enrollmentSettings.IsEnabledAsync(AepEnrollmentMode.SharedKeyFile, wait.Token))
                            throw new DeclarativeBootstrapException($"[enrollment_mode_disabled] {lastDiagnostic}", exception);
                        missing = true;
                    }
                }
                if (!missing) return;
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, wait.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DeclarativeBootstrapException($"[bootstrap_dependency_timeout] Required extension contributions were not ready within {timeout}. {lastDiagnostic}");
        }
    }
}
