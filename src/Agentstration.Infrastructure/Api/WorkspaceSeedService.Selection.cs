using System.Text;
using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Work;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;

namespace Agentstration.Web.Hosting;

public sealed partial class WorkspaceSeedService
{
    internal async Task<LoadedBootstrapSelection> LoadSelectionAsync(WorkspaceSeedSelection selection,
        BootstrapApplicationTarget? target, CancellationToken token)
    {
        if (!current.IsInitialized || current.AccessMode != ControlPlaneAccessMode.Workspace) throw Invalid("seed_workspace_required");
        var actor = current.Current.PrincipalId;
        var context = await AuthorizeTargetAsync(target, actor, token);
        using var scope = scopes.Push(context);
        var stored = await StoredAsync(selection.Id, target!, actor, token);
        if (!DigestValue(selection.ArchiveDigest) || selection.ArchiveDigest != stored.Provenance.ArchiveDigest) throw Invalid("seed_integrity_mismatch");
        await VerifyArtifactAsync(stored.Artifact, token);
        var resolution = await RequirementsAsync(stored, token);
        var loaded = await profiles.MaterializeSnapshotAsync(stored.Artifact, stored.Manifest.Profile, stored.Manifest.Path, null, resolution, token);
        try
        {
            ValidateProfile(loaded.Profiles.Single(), stored.Manifest);
            if (loaded.Profiles.Single().Summary.Digest != stored.Profile.Digest) throw Invalid("seed_integrity_mismatch");
            return loaded;
        }
        catch { await loaded.DisposeAsync(); throw; }
    }

    private async Task<WorkspaceSeedResolution> RequirementsAsync(WorkspaceSeedStoredContent stored, CancellationToken token)
    {
        var required = stored.Manifest.Requirements;
        var consumer = ResourceScopeRef.Workspace(stored.Provenance.Target.WorkspaceId!.Value);
        var facts = new List<WorkspaceSeedPrerequisite>(); var fingerprints = new List<string>();
        foreach (var contribution in required.Contributions)
        {
            try
            {
                var resolved = await extensionReferences.ResolveContributionAsync(contribution, consumer, token);
                if (contribution.Kind == ToolResourceKinds.Tool && contribution.ContributionId == "git.repository")
                {
                    var owner = resolved.Reference.ScopeRef ?? consumer;
                    var tool = (await resources.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(owner,
                        resolved.Reference.Namespace ?? ResourceNamespace.Default, ToolResourceKinds.Tool, resolved.Reference.Name), token))?.Value;
                    if (tool is null || !GitWorkProjectSharingGateway.HasRequiredCapabilities(tool)) throw Invalid("seed_git_capability_missing");
                }
                facts.Add(new(contribution.Kind, contribution.ContributionId, true)); fingerprints.Add(resolved.Fingerprint);
            }
            catch (Exception exception) when (exception is BootstrapDependencyException or DeclarativeBootstrapException or AuthorizationDeniedException)
            {
                var code = exception is BootstrapDependencyException dependency ? dependency.Code
                    : exception is AuthorizationDeniedException ? "seed_prerequisite_access_denied" : "seed_git_capability_missing";
                facts.Add(new(contribution.Kind, contribution.ContributionId, false, code));
            }
        }
        var nativeProfileAvailable = false;
        foreach (var provider in required.ModelProviders) _ = await InstalledAsync<ModelProviderResource>(provider, ModelResourceKinds.ModelProvider);
        foreach (var runtime in required.RuntimeProfiles)
        {
            var profile = await InstalledAsync<RuntimeProfileResource>(runtime, RuntimeProfileResourceKinds.RuntimeProfile);
            nativeProfileAvailable |= profile?.Definition.RuntimeType == required.Runtime.Name;
        }
        if (!nativeProfileAvailable) facts.Add(new("RuntimeProfile", required.Runtime.Name, false, "seed_runtime_profile_missing"));
        var availableWorker = workers.ListRegistrations().Any(worker => worker.LastSeenAt >= clock.GetUtcNow() - TimeSpan.FromMinutes(2)
            && worker.Capabilities.Any(capability => capability.RuntimeKind == required.Runtime.Name && capability.CapabilityVersion == required.Runtime.CapabilityVersion
                && capability.ExecutionMaterialVersions.Contains(required.Runtime.MaterialVersion)));
        facts.Add(new("Worker", required.Runtime.Name, availableWorker, availableWorker ? null : "seed_native_worker_missing"));
        // Heartbeats and capacity do not change the reviewed configuration or select a placement preference.
        fingerprints.Add(JsonSerializer.Serialize(new { required.Runtime, Available = availableWorker }, Json));
        return new(stored.Provenance, facts, Digest(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { facts, fingerprints }, Json))));

        async Task<T?> InstalledAsync<T>(ResourceReference? reference, string kind) where T : Resource
        {
            if (reference is null || reference.ScopeRef is { } owner && owner != ResourceScopeRef.Instance || reference.Name is not { Length: > 0 and <= 128 })
            { facts.Add(new(kind, reference?.Name ?? "", false, "seed_prerequisite_invalid")); return null; }
            using var metadata = scopes.PushSystem();
            var storedResource = await resources.GetExactAsync<T>(ScopedResourceAddress.Create(ResourceScopeRef.Instance,
                reference.Namespace ?? ResourceNamespace.Default, kind, reference.Name), token);
            if (storedResource is null)
            { facts.Add(new(kind, reference.Name, false, "seed_prerequisite_missing")); return null; }
            var value = storedResource.Value;
            var identity = new ResourceAccessIdentity(value.Uid, ResourceScopeRef.Instance, value.Namespace, kind, value.Generation);
            var allowed = (await access.EvaluateAsync(identity, consumer, ResourceAccessAction.Discover, token)).Allowed
                && (await access.EvaluateAsync(identity, consumer, ResourceAccessAction.Use, token)).Allowed;
            if (value is ModelProviderResource model)
            {
                var extension = model.Definition?.Extension;
                var registration = extension is null ? null : (await resources.GetExactAsync<ExtensionRegistrationResource>(ScopedResourceAddress.Create(
                    extension.ScopeRef ?? ResourceScopeRef.Instance, extension.Namespace ?? ResourceNamespace.Default, ExtensionKinds.ExtensionRegistration, extension.Name), token))?.Value;
                if (registration?.Definition.Enabled != true)
                { facts.Add(new(kind, reference.Name, false, "seed_model_provider_unavailable")); return null; }
            }
            facts.Add(new(kind, reference.Name, allowed, allowed ? null : "seed_prerequisite_access_denied"));
            fingerprints.Add(JsonSerializer.Serialize(new { Kind = kind, Name = reference.Name, value.Namespace, value.Uid, value.Generation, storedResource.ETag, Allowed = allowed }, Json));
            return allowed ? value : null;
        }
    }
}
