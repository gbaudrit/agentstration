using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Hosting;

public sealed partial class BootstrapProfileManagementService
{
    private sealed record PreparedProfile(BootstrapProfileApplication Application, LoadedBootstrapProfile Profile,
        BootstrapApplicationTarget? ResolvedTarget, string TargetLabel);

    private void ValidatePlanRequest(BootstrapProfileSelection selection)
    {
        var applications = selection.Applications ?? [];
        if (applications.Count is 0 || applications.Count > catalog.MaximumSelectedProfiles
            || selection.Profiles.Count != 0 || selection.Target is not null || selection.Source is not null || selection.Bindings is { Count: > 0 }
            || applications.Any(item => item is null)
            || applications.Select(item => item.Profile).Distinct(StringComparer.Ordinal).Count() != applications.Count)
            throw new DeclarativeBootstrapException("bootstrap_selection_invalid");
    }

    private async Task<IReadOnlyList<PreparedProfile>> LoadPlanAsync(BootstrapProfileSelection selection, Guid actor, CancellationToken token, bool requireBindings = true)
    {
        ValidatePlanRequest(selection);
        var applications = selection.Applications!;
        var profiles = await catalog.LoadAsync(applications.Select(item => item.Profile).ToArray(), token);
        var ordered = applications.Select((application, index) => (Application: application, Profile: profiles[index]))
            .OrderBy(item => item.Profile.Summary.Scope).ToArray();
        var result = new List<PreparedProfile>();
        var plannedTargets = new HashSet<BootstrapPlannedTarget>();
        foreach (var (application, profile) in ordered)
        {
            bootstrap.ValidateStartupProfiles([profile]);
            ValidatePlanBindings(application, profile.Summary, requireBindings);
            if (application.Target is not null && application.PlannedTarget is not null)
                throw new DeclarativeBootstrapException("bootstrap_target_invalid");
            var scope = profile.Summary.Scope;
            if (scope == BootstrapProfileScope.Instance && (application.Target is not null || application.PlannedTarget is not null)
                || scope == BootstrapProfileScope.Tenant && (application.PlannedTarget?.WorkspaceName is not null || application.Target?.WorkspaceId is not null)
                || scope == BootstrapProfileScope.Workspace && application.PlannedTarget is { WorkspaceName: null })
                throw new DeclarativeBootstrapException("bootstrap_target_invalid");
            BootstrapApplicationTarget? target = application.Target;
            string? tenantName = null;
            string label = "/instance";
            if (application.PlannedTarget is { } named)
            {
                if (string.IsNullOrWhiteSpace(named.TenantName) || named.TenantName.Length > 128 || named.WorkspaceName?.Length > 128
                    || OperationalTopologyIdentities.IsReservedTenantName(named.TenantName)
                    || named.WorkspaceName is { } name && (string.IsNullOrWhiteSpace(name) || OperationalTopologyIdentities.IsReservedWorkspaceName(name)))
                    throw new DeclarativeBootstrapException("bootstrap_target_invalid");
                var namedTenant = await identities.FindTenantByNameAsync(named.TenantName, token);
                if (namedTenant is not null && namedTenant.Status != TenantStatus.Active)
                    throw new DeclarativeBootstrapException("bootstrap_target_invalid");
                target = await FindNamedTargetAsync(named, token);
                if (target is null && !plannedTargets.Contains(named))
                    throw new DeclarativeBootstrapException("bootstrap_target_not_planned");
                tenantName = named.TenantName;
                label = TargetPath(named);
            }
            else if (scope != BootstrapProfileScope.Instance)
            {
                if (target?.TenantId is null || scope == BootstrapProfileScope.Workspace && target.WorkspaceId is null)
                    throw new DeclarativeBootstrapException("bootstrap_target_invalid");
            }
            if (target is not null)
            {
                _ = await ResolveTargetContextAsync(target, actor, token);
                var tenant = await identities.GetTenantAsync(target.TenantId!.Value, token);
                tenantName = tenant!.Name;
                var workspace = target.WorkspaceId is { } workspaceId
                    ? await identities.GetWorkspaceAsync(tenant.Id, workspaceId, token) : null;
                label = TargetPath(new(tenant.Name, workspace?.Name));
            }
            result.Add(new(application, profile, target, label));
            foreach (var topology in profile.Summary.Topology)
            {
                if (topology.Kind == "Tenant") plannedTargets.Add(new(topology.Name));
                else if ((topology.TenantName ?? tenantName) is { } owner)
                    plannedTargets.Add(new(owner, topology.Name));
            }
        }
        foreach (var consumer in result)
            foreach (var binding in consumer.Application.Bindings ?? [])
                if (binding.TargetProfile is { } producer)
                {
                    var index = result.FindIndex(item => item.Application.Profile == producer);
                    var declaration = consumer.Profile.Summary.Bindings.Single(item => item.Name == binding.Name);
                    if (index < 0 || index > result.IndexOf(consumer) || !result[index].Profile.Resources.Any(source =>
                        source.Resource.Kind == declaration.TargetKind.ToString() && source.Resource.Metadata.Name == binding.Target.Name
                        && source.Resource.Metadata.Namespace == (binding.Target.Namespace ?? ResourceNamespace.Default)))
                        throw new DeclarativeBootstrapException("bootstrap_bindings_invalid");
                }
        return result;
    }

    private static void ValidatePlanBindings(BootstrapProfileApplication application, BootstrapProfileSummary profile, bool requireBindings)
    {
        var bindings = application.Bindings ?? [];
        if (bindings.Count > 64 || bindings.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != bindings.Count
            || bindings.Any(item => item is null || item.Target is null || item.Profile != application.Profile || item.TargetValue is not null
                || !profile.Bindings.Any(declaration => declaration.Name == item.Name && declaration.ExtensionTarget is null)))
            throw new DeclarativeBootstrapException("bootstrap_bindings_invalid");
        if (!requireBindings) return;
        foreach (var declaration in profile.Bindings.Where(item => item.Required && item.DefaultTarget is null && item.ExtensionTarget is null))
            if (!bindings.Any(item => item.Name == declaration.Name && !string.IsNullOrWhiteSpace(item.Target.Name)))
                throw new DeclarativeBootstrapException("bootstrap_bindings_invalid");
    }

    private async Task<BootstrapApplicationTarget?> FindNamedTargetAsync(BootstrapPlannedTarget named, CancellationToken token)
    {
        var tenant = await identities.FindTenantByNameAsync(named.TenantName, token);
        if (tenant is null) return null;
        if (named.WorkspaceName is null) return new(tenant.Id);
        var workspace = await identities.FindWorkspaceByNameAsync(tenant.Id, named.WorkspaceName, token);
        return workspace is null ? null : new(tenant.Id, workspace.Id);
    }

    private static string TargetPath(BootstrapPlannedTarget target) => "/tenants/" + target.TenantName
        + (target.WorkspaceName is { } workspace ? "/workspaces/" + workspace : string.Empty);

    private async Task<BootstrapCompositionPreview> PreviewPlanAsync(BootstrapProfileSelection selection, Guid actor, CancellationToken token)
    {
        var plan = await LoadPlanAsync(selection, actor, token);
        var resources = new List<BootstrapResourcePreview>();
        var applications = new List<BootstrapProfileApplicationPreview>();
        var predecessorCreates = false;
        foreach (var item in plan)
        {
            var deferred = item.Application.PlannedTarget is not null && item.ResolvedTarget is null
                || (item.Application.Bindings ?? []).Any(binding => binding.TargetProfile is { } producer
                    && plan.Any(candidate => candidate.Application.Profile == producer && candidate.Application.PlannedTarget is not null && candidate.ResolvedTarget is null));
            BootstrapCompositionPreview? preview = null;
            if (!deferred)
            {
                using var scope = PushTargetScope(await ResolveTargetContextAsync(item.ResolvedTarget, actor, token));
                try
                {
                    preview = await bootstrap.PreviewAsync(new([item.Application.Profile], item.ResolvedTarget, await ResolvePlanBindingsAsync(item.Application, plan, token)), token);
                    deferred = predecessorCreates && !preview.CanApply;
                }
                catch (DeclarativeBootstrapException) when (predecessorCreates) { deferred = true; }
            }
            var rows = preview?.Resources ?? item.Profile.Resources.Select(source => new BootstrapResourcePreview(
                item.Application.Profile, source.Location, source.Resource.Kind, source.Resource.Metadata.Name, BootstrapResourceDisposition.Create)).ToArray();
            resources.AddRange(rows.Select(row => row with
            {
                TargetLabel = item.TargetLabel,
                Disposition = deferred && row.Disposition == BootstrapResourceDisposition.Invalid ? BootstrapResourceDisposition.Create : row.Disposition,
                Message = deferred ? "bootstrap_validation_after_dependencies" : row.Message
            }));
            applications.Add(new(item.Application.Profile, item.Profile.Summary.Scope, item.TargetLabel, deferred, deferred ? null : preview?.Digest) { ResolvedTarget = item.ResolvedTarget });
            predecessorCreates |= deferred || rows.Any(row => row.Disposition == BootstrapResourceDisposition.Create);
        }
        // Pin the ordered source bytes, original target identities and explicit bindings, not mutable outcomes.
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan.Select((item, index) => new
        {
            item.Application,
            item.Profile.Summary.Digest,
            item.ResolvedTarget,
            ResolutionDigest = applications[index].Digest
        })))).ToLowerInvariant();
        return new(plan.Select(item => item.Profile.Summary).ToArray(), BootstrapProfileScope.Instance, null,
            plan.SelectMany(item => item.Application.Bindings ?? []).ToArray(), digest, resources)
        { Applications = applications };
    }

    private async Task<BootstrapExecutionResult> ExecutePlanAsync(BootstrapProfileSelection selection, Guid actor,
        string expectedDigest, Func<BootstrapExecutionProgress, CancellationToken, Task> report, CancellationToken token)
    {
        var preview = await PreviewPlanAsync(selection, actor, token);
        if (!string.Equals(preview.Digest, expectedDigest, StringComparison.Ordinal) || !preview.CanApply)
            return new(preview, [], "bootstrap_application_invalid");
        var resources = new List<BootstrapAppliedResource>();
        await report(new(preview, [], null), token);
        var originals = preview.Profiles.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var profile in preview.Profiles)
        {
            var application = selection.Applications!.Single(item => item.Profile == profile.Name);
            var first = preview.Resources.FirstOrDefault(item => item.Profile == profile.Name);
            try
            {
                token.ThrowIfCancellationRequested();
                await EnsurePlatformAdministratorAsync(actor, token);
                // Re-read every profile before executing it. An earlier phase must not hide source drift.
                var loaded = (await catalog.LoadAsync([profile.Name], token)).Single();
                if (loaded.Summary.Digest != originals[profile.Name].Digest)
                    return new(preview, resources, "bootstrap_application_invalid");
                var target = application.PlannedTarget is { } named
                    ? await FindNamedTargetAsync(named, token) ?? throw new DeclarativeBootstrapException("bootstrap_target_not_ready")
                    : application.Target;
                using var scope = PushTargetScope(await ResolveTargetContextAsync(target, actor, token));
                preview = preview with { Applications = preview.Applications.Select(item => item.Profile == profile.Name ? item with { ResolvedTarget = target } : item).ToArray() };
                if (first is not null) await report(new(preview, resources.ToArray(), first), token);
                var completed = resources.ToArray();
                var execution = await bootstrap.ExecuteAsync(new([profile.Name], target, await ResolvePlanBindingsAsync(application, await LoadPlanAsync(selection, actor, token), token)), token,
                    expectedDigest: preview.Applications.Single(item => item.Profile == profile.Name).Digest,
                    reportProgress: (progress, cancellation) => report(new(preview,
                        completed.Concat(progress.Resources).ToArray(), progress.CurrentResource is { } current
                            ? current with { TargetLabel = first?.TargetLabel } : null), cancellation), waitForDependencies: true);
                resources.AddRange(execution.Resources);
                if (execution.Error is not null)
                {
                    if (execution.Resources.Count == 0 && first is not null)
                        resources.Add(new(first.Profile, first.Location, first.Kind, first.Name, BootstrapResourceDisposition.Failed, "bootstrap_application_invalid") { Failure = new("validation", "bootstrap_application_invalid", Guid.NewGuid().ToString("N")) });
                    return new(preview, resources, execution.Error);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var diagnostic = Agentstration.Infrastructure.Bootstrap.BootstrapFailureDiagnostics.Describe(exception);
                logger?.LogWarning("Bootstrap profile {Profile} failed ({ReasonCode}, diagnostic {DiagnosticId}).", profile.Name, diagnostic.Code, diagnostic.DiagnosticId);
                if (first is not null)
                    resources.Add(new(first.Profile, first.Location, first.Kind, first.Name, BootstrapResourceDisposition.Failed, "bootstrap_resource_failed") { Failure = diagnostic });
                await report(new(preview, resources.ToArray(), null), CancellationToken.None);
                return new(preview, resources, "bootstrap_resource_failed");
            }
        }
        return new(preview, resources);
    }
}

// Plan bindings refer to a producer profile; physical owner IDs are resolved after its topology exists.
public sealed partial class BootstrapProfileManagementService
{
    public async Task<IReadOnlyList<BootstrapBindingTargetOption>> GetPlanBindingTargetsAsync(
        BootstrapBindingTargetsRequest request, Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(actorPrincipalId, cancellationToken);
        if (request.Target is not null || request.Source is not null || request.Profiles is { Count: > 0 } || !Enum.IsDefined(request.TargetKind))
            throw new DeclarativeBootstrapException("bootstrap_bindings_invalid");
        var plan = await LoadPlanAsync(new([], Applications: request.Applications), actorPrincipalId, cancellationToken, requireBindings: false);
        var consumerIndex = plan.ToList().FindIndex(item => item.Application.Profile == request.ConsumerProfile);
        if (consumerIndex < 0) throw new DeclarativeBootstrapException("bootstrap_bindings_invalid");
        var consumer = plan[consumerIndex];
        var existing = consumer.ResolvedTarget?.WorkspaceId is not null
            ? await GetBindingTargetsAsync(consumer.ResolvedTarget, request.TargetKind, [consumer.Application.Profile], actorPrincipalId, cancellationToken)
            : [];
        var options = new List<BootstrapBindingTargetOption>(existing);
        foreach (var producer in plan.Take(consumerIndex + 1))
        {
            if (!VisibleInPlan(producer, consumer, request.TargetKind)) continue;
            foreach (var source in producer.Profile.Resources.Where(source => source.Resource.Kind == request.TargetKind.ToString()))
                options.Add(new(source.Resource.Metadata.Name, source.Resource.Metadata.Namespace.Value, source.Resource.Metadata.Name,
                    Planned: true, TargetProfile: producer.Application.Profile));
        }
        return options.DistinctBy(option => (option.TargetProfile, option.ScopeRef, option.Namespace, option.Name)).ToArray();
    }

    private static bool VisibleInPlan(PreparedProfile producer, PreparedProfile consumer, BootstrapBindingTargetKind kind)
    {
        if (producer.Profile.Summary.Scope == BootstrapProfileScope.Instance) return true;
        var producerTenant = producer.TargetLabel.Split("/workspaces/", StringSplitOptions.None)[0];
        var consumerTenant = consumer.TargetLabel.Split("/workspaces/", StringSplitOptions.None)[0];
        return producer.Profile.Summary.Scope == BootstrapProfileScope.Tenant || kind is BootstrapBindingTargetKind.Parameter or BootstrapBindingTargetKind.Secret ? producerTenant == consumerTenant
            : producer.TargetLabel == consumer.TargetLabel;
    }

    private async Task<IReadOnlyList<BootstrapBindingSelection>> ResolvePlanBindingsAsync(BootstrapProfileApplication consumer,
        IReadOnlyList<PreparedProfile> plan, CancellationToken token)
    {
        var result = new List<BootstrapBindingSelection>();
        var consumerIndex = plan.ToList().FindIndex(item => item.Application.Profile == consumer.Profile);
        foreach (var binding in consumer.Bindings ?? [])
        {
            if (binding.TargetProfile is not { } name) { result.Add(binding); continue; }
            var producerIndex = plan.ToList().FindIndex(item => item.Application.Profile == name);
            if (producerIndex < 0 || producerIndex > consumerIndex) throw new DeclarativeBootstrapException("bootstrap_bindings_invalid");
            var producer = plan[producerIndex];
            var declaration = plan[consumerIndex].Profile.Summary.Bindings.Single(item => item.Name == binding.Name);
            if (!producer.Profile.Resources.Any(source => source.Resource.Kind == declaration.TargetKind.ToString()
                && source.Resource.Metadata.Name == binding.Target.Name
                && source.Resource.Metadata.Namespace == (binding.Target.Namespace ?? ResourceNamespace.Default)))
                throw new DeclarativeBootstrapException("bootstrap_bindings_invalid");
            var target = producer.Application.PlannedTarget is { } named
                ? await FindNamedTargetAsync(named, token) ?? throw new DeclarativeBootstrapException("bootstrap_target_not_ready")
                : producer.Application.Target;
            var owner = declaration.TargetKind is BootstrapBindingTargetKind.Parameter or BootstrapBindingTargetKind.Secret
                ? ResourceScopeRef.Tenant(target!.TenantId!.Value)
                : producer.Profile.Summary.Scope switch
                {
                    BootstrapProfileScope.Instance => ResourceScopeRef.Instance,
                    BootstrapProfileScope.Tenant => ResourceScopeRef.Tenant(target!.TenantId!.Value),
                    _ => ResourceScopeRef.Workspace(target!.WorkspaceId!.Value)
                };
            result.Add(binding with { Target = binding.Target with { ScopeRef = owner }, TargetProfile = null });
        }
        return result;
    }
}
