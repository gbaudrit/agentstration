using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Tools;

namespace Agentstration.Knowledge;

public sealed class KnowledgeSourceProfileValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class KnowledgeSourceProfileService(
    IResourceStore store,
    IResourceReferenceResolver references,
    IResourceScopeOperations scopeOperations,
    IKnowledgeFlowResolver flows,
    ToolSetService toolSets,
    ICurrentRequestContext requestContext,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public const int MaximumConfigurationSchemaBytes = 64 * 1024;
    private const int MaximumProfilesPerWorkspace = 1000;

    public Task<IReadOnlyList<StoredResource<KnowledgeSourceProfileResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<KnowledgeSourceProfileResource>(KnowledgeResourceKinds.KnowledgeSourceProfile, cancellationToken);

    public Task<StoredResource<KnowledgeSourceProfileResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetAsync<KnowledgeSourceProfileResource>(new(KnowledgeResourceKinds.KnowledgeSourceProfile, name, @namespace), cancellationToken);

    public async Task<IReadOnlyList<StoredResource<KnowledgeSourceProfileRevisionResource>>> ListRevisionsAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        var profile = await GetAsync(@namespace, name, cancellationToken)
            ?? throw NotFound(@namespace, name);
        var scopeRef = RequireScope(profile.Value);
        return (await store.ListExactAsync<KnowledgeSourceProfileRevisionResource>(scopeRef,
                KnowledgeResourceKinds.KnowledgeSourceProfileRevision, 0, MaximumProfilesPerWorkspace, cancellationToken))
            .Where(value => value.Value.ProfileUid == profile.Value.Uid)
            .OrderByDescending(value => value.Value.PublishedAt)
            .ToArray();
    }

    public async Task<StoredResource<KnowledgeSourceProfileRevisionResource>?> GetRevisionAsync(
        ResourceNamespace @namespace,
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        var profile = await GetAsync(@namespace, name, cancellationToken);
        if (profile is null) return null;
        var stored = await store.GetExactAsync<KnowledgeSourceProfileRevisionResource>(Scoped(RequireScope(profile.Value),
            @namespace, KnowledgeResourceKinds.KnowledgeSourceProfileRevision, RevisionName(name, version)), cancellationToken);
        return stored is not null && stored.Value.ProfileUid == profile.Value.Uid
            && string.Equals(stored.Value.Version, version, StringComparison.Ordinal)
            ? stored
            : null;
    }

    public async Task<StoredResource<KnowledgeSourceProfileResource>> CreateAsync(
        KnowledgeSourceProfileResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(KnowledgeResourceKinds.KnowledgeSourceProfile);
        var desired = resource with
        {
            ScopeRef = scopeRef,
            Generation = 1,
            ActiveVersion = null,
            Status = Status("DraftReady", "The profile draft is valid and has not been published.")
        };
        Validate(desired);
        ResourceScopePolicy.EnsureAllowed(desired, scopeRef);
        _ = await ResolveDefinitionAsync(desired, cancellationToken);
        var stored = await scopeOperations.WriteAsync(desired, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, desired, null, true, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.KnowledgeSourceProfileCreated, scopeRef, stored.Value.Name, cancellationToken);
        if (stored.Value.Definition.Publish)
        {
            _ = await PublishAsync(stored.Value.Namespace, stored.Value.Name, new PublishKnowledgeSourceProfileRequest
            {
                Version = stored.Value.Definition.Version,
                Activate = stored.Value.Definition.Activate
            }, cancellationToken);
            stored = await GetAsync(stored.Value.Namespace, stored.Value.Name, cancellationToken) ?? stored;
        }
        return stored;
    }

    public async Task<StoredResource<KnowledgeSourceProfileResource>> PutAsync(
        ResourceNamespace @namespace,
        string name,
        KnowledgeSourceProfileProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken) ?? throw NotFound(@namespace, name);
        var desired = existing.Value with
        {
            Definition = definition,
            Generation = checked(existing.Value.Generation + 1),
            Status = Status("DraftReady", "The profile draft is valid.")
        };
        Validate(desired);
        _ = await ResolveDefinitionAsync(desired, cancellationToken);
        var scopeRef = RequireScope(existing.Value);
        var stored = await scopeOperations.WriteAsync(existing.Value, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, desired, ifMatch, false, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.KnowledgeSourceProfileUpdated, scopeRef, stored.Value.Name, cancellationToken);
        if (stored.Value.Definition.Publish
            && await GetRevisionAsync(stored.Value.Namespace, stored.Value.Name, stored.Value.Definition.Version, cancellationToken) is null)
        {
            _ = await PublishAsync(stored.Value.Namespace, stored.Value.Name, new PublishKnowledgeSourceProfileRequest
            {
                Version = stored.Value.Definition.Version,
                Activate = stored.Value.Definition.Activate
            }, cancellationToken);
            stored = await GetAsync(stored.Value.Namespace, stored.Value.Name, cancellationToken) ?? stored;
        }
        return stored;
    }

    public async Task<StoredResource<KnowledgeSourceProfileRevisionResource>> PublishAsync(
        ResourceNamespace @namespace,
        string name,
        PublishKnowledgeSourceProfileRequest request,
        CancellationToken cancellationToken)
    {
        ValidateDefaults(request.ConfigurationDefaults);
        var profile = await GetAsync(@namespace, name, cancellationToken) ?? throw NotFound(@namespace, name);
        if (!profile.Value.Definition.Enabled)
            throw Error("knowledge_source_profile_disabled", "A disabled Knowledge Source Profile cannot be published.");
        if (!string.Equals(profile.Value.Definition.Version, request.Version, StringComparison.Ordinal))
            throw Error("knowledge_source_profile_version_mismatch", "The requested version must match the profile draft version.");

        var publishedDefinition = profile.Value.Definition with { Publish = false, Activate = false };
        var resolution = await ResolveDefinitionAsync(profile.Value with { Definition = publishedDefinition }, cancellationToken);
        var impacts = await SourceImpactsAsync(profile.Value, resolution.ConfigurationSchema,
            request.ConfigurationDefaults, cancellationToken);
        ThrowIfIncompatible(impacts);
        var definitionHash = DefinitionHash(publishedDefinition, resolution);
        var existing = await GetRevisionAsync(@namespace, name, request.Version, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Value.DefinitionHash, definitionHash, StringComparison.Ordinal))
                throw Error("knowledge_source_profile_revision_immutable",
                    $"Published profile revision '{request.Version}' is immutable.");
            if (request.Activate)
                _ = await ActivateCoreAsync(profile, existing, request.ConfigurationDefaults, cancellationToken);
            return existing;
        }

        var context = requestContext.IsInitialized ? requestContext.Current : null;
        var revision = new KnowledgeSourceProfileRevisionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSourceProfileRevision,
            Metadata = new ResourceMetadata
            {
                Name = RevisionName(name, request.Version),
                Namespace = @namespace,
                Tags = profile.Value.Metadata.Tags,
                Annotations = profile.Value.Metadata.Annotations
            },
            ScopeRef = RequireScope(profile.Value),
            Generation = 1,
            Status = Status("Published", "The profile revision is immutable."),
            ProfileUid = profile.Value.Uid,
            ProfileName = profile.Value.Name,
            ProfileGeneration = profile.Value.Generation,
            Version = request.Version,
            DefinitionHash = definitionHash,
            PublishedAt = timeProvider.GetUtcNow(),
            PublishedBy = context?.PrincipalId ?? Guid.Empty,
            Definition = publishedDefinition,
            Resolution = resolution with { DefinitionHash = definitionHash }
        };
        var scopeRef = RequireScope(profile.Value);
        var stored = await scopeOperations.WriteAsync(profile.Value, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.CreateImmutableAsync(revision, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.KnowledgeSourceProfileRevisionPublished, scopeRef,
            $"{name}:{request.Version}", cancellationToken);
        if (request.Activate)
            _ = await ActivateCoreAsync(profile, stored, request.ConfigurationDefaults, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<KnowledgeSourceProfileResource>> ActivateAsync(
        ResourceNamespace @namespace,
        string name,
        ActivateKnowledgeSourceProfileRequest request,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        ValidateDefaults(request.ConfigurationDefaults);
        var profile = await GetAsync(@namespace, name, cancellationToken) ?? throw NotFound(@namespace, name);
        if (ifMatch is not null && !string.Equals(profile.ETag, ifMatch, StringComparison.Ordinal))
            throw new ResourceConcurrencyException("The Knowledge Source Profile changed before activation.");
        var revision = await GetRevisionAsync(@namespace, name, request.Version, cancellationToken)
            ?? throw Error("knowledge_source_profile_revision_not_found",
                $"Published profile revision '{@namespace}/{name}:{request.Version}' was not found.");
        return await ActivateCoreAsync(profile, revision, request.ConfigurationDefaults, cancellationToken);
    }

    public async Task<ResolvedKnowledgeSourceProfile> ResolveActiveAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        ResourceReference reference,
        CancellationToken cancellationToken)
    {
        var profile = await references.ResolveAsync<KnowledgeSourceProfileResource>(reference, ownerNamespace,
            KnowledgeResourceKinds.KnowledgeSourceProfile, scopeRef, cancellationToken)
            ?? throw Error("knowledge_source_profile_not_found", $"Knowledge Source Profile '{reference.Name}' was not found.");
        if (!profile.Value.Definition.Enabled)
            throw Error("knowledge_source_profile_disabled", $"Knowledge Source Profile '{profile.Value.Address}' is disabled.");
        var version = profile.Value.ActiveVersion
            ?? throw Error("knowledge_source_profile_not_published", $"Knowledge Source Profile '{profile.Value.Address}' has no active revision.");
        var revision = await store.GetExactAsync<KnowledgeSourceProfileRevisionResource>(Scoped(RequireScope(profile.Value),
            profile.Value.Namespace, KnowledgeResourceKinds.KnowledgeSourceProfileRevision,
            RevisionName(profile.Value.Name, version)), cancellationToken)
            ?? throw Error("knowledge_source_profile_revision_not_found",
                $"Active profile revision '{profile.Value.Address}:{version}' was not found.");
        return revision.Value.Resolution;
    }

    public async Task<KnowledgeSourceProfileApplicationPlan> PreviewApplicationAsync(
        ResourceNamespace targetNamespace,
        string targetName,
        PreviewKnowledgeSourceProfileApplicationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateVersion(request.TargetVersion);
        ValidateDefaults(request.ConfigurationDefaults);
        var target = await GetAsync(targetNamespace, targetName, cancellationToken) ?? throw NotFound(targetNamespace, targetName);
        var source = await references.ResolveAsync<KnowledgeSourceProfileResource>(request.SourceProfile, targetNamespace,
            KnowledgeResourceKinds.KnowledgeSourceProfile, RequireScope(target.Value), cancellationToken)
            ?? throw Error("knowledge_source_profile_application_source_not_found", "The source profile was not found.");
        var sourceRevision = await store.GetExactAsync<KnowledgeSourceProfileRevisionResource>(Scoped(RequireScope(source.Value),
            source.Value.Namespace, KnowledgeResourceKinds.KnowledgeSourceProfileRevision,
            RevisionName(source.Value.Name, request.SourceVersion)), cancellationToken);
        if (sourceRevision is null || sourceRevision.Value.ProfileUid != source.Value.Uid)
            throw Error("knowledge_source_profile_application_revision_not_found",
                $"Published source profile revision '{source.Value.Address}:{request.SourceVersion}' was not found.");
        return await BuildApplicationPlanAsync(target.Value, sourceRevision.Value, request, cancellationToken);
    }

    public async Task<StoredResource<KnowledgeSourceProfileRevisionResource>> ApplyAsync(
        ResourceNamespace targetNamespace,
        string targetName,
        PreviewKnowledgeSourceProfileApplicationRequest request,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var plan = await PreviewApplicationAsync(targetNamespace, targetName, request, cancellationToken);
        if (!plan.Ready)
            throw Error("knowledge_source_profile_application_not_ready", string.Join(' ', plan.Issues));
        var target = await GetAsync(targetNamespace, targetName, cancellationToken) ?? throw NotFound(targetNamespace, targetName);
        if (ifMatch is not null && !string.Equals(target.ETag, ifMatch, StringComparison.Ordinal))
            throw new ResourceConcurrencyException("The target Knowledge Source Profile changed before application.");
        var source = await references.ResolveAsync<KnowledgeSourceProfileResource>(request.SourceProfile, targetNamespace,
            KnowledgeResourceKinds.KnowledgeSourceProfile, RequireScope(target.Value), cancellationToken)!;
        var sourceRevision = await store.GetExactAsync<KnowledgeSourceProfileRevisionResource>(Scoped(RequireScope(source!.Value),
            source.Value.Namespace, KnowledgeResourceKinds.KnowledgeSourceProfileRevision,
            RevisionName(source.Value.Name, request.SourceVersion)), cancellationToken)
            ?? throw Error("knowledge_source_profile_application_revision_not_found", "The source profile revision was not found.");
        var copied = sourceRevision.Value.Definition with
        {
            DisplayName = target.Value.Definition.DisplayName,
            Description = target.Value.Definition.Description,
            Version = request.TargetVersion,
            Publish = false,
            Activate = true,
            AppliedFrom = new KnowledgeSourceProfileApplicationProvenance
            {
                ProfileName = source.Value.Name,
                ProfileNamespace = source.Value.Namespace,
                ProfileUid = source.Value.Uid,
                Version = sourceRevision.Value.Version,
                DefinitionHash = sourceRevision.Value.DefinitionHash,
                ExtensionOrigin = source.Value.Metadata.Annotations.GetValueOrDefault("agentstration.io/extension"),
                CorrelationId = request.CorrelationId,
                AppliedAt = timeProvider.GetUtcNow()
            }
        };
        _ = await PutAsync(targetNamespace, targetName, copied, target.ETag, cancellationToken);
        var published = await PublishAsync(targetNamespace, targetName, new PublishKnowledgeSourceProfileRequest
        {
            Version = request.TargetVersion,
            Activate = true,
            ConfigurationDefaults = request.ConfigurationDefaults
        }, cancellationToken);
        await AuditAsync(SecurityAuditActions.KnowledgeSourceProfileApplied, RequireScope(target.Value),
            $"{source.Value.Address}:{request.SourceVersion}->{target.Value.Address}:{request.TargetVersion}", cancellationToken);
        return published;
    }

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var profile = await GetAsync(@namespace, name, cancellationToken) ?? throw NotFound(@namespace, name);
        var scopeRef = RequireScope(profile.Value);
        var sources = await ReferencingSourcesAsync(profile.Value, cancellationToken);
        if (sources.Count != 0)
            throw Error("knowledge_source_profile_in_use",
                $"Knowledge Source Profile '{profile.Value.Address}' is referenced by {sources.Count} Knowledge Source(s).");
        var revisions = await ListRevisionsAsync(@namespace, name, cancellationToken);
        await scopeOperations.WriteAsync(profile.Value, scopeRef, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            foreach (var revision in revisions)
                await store.DeleteExactAsync(Scoped(scopeRef, @namespace, KnowledgeResourceKinds.KnowledgeSourceProfileRevision,
                    revision.Value.Name), revision.ETag, token);
            await store.DeleteExactAsync(Scoped(scopeRef, @namespace, KnowledgeResourceKinds.KnowledgeSourceProfile, name),
                ifMatch, token);
            return true;
        }, cancellationToken);
        await AuditAsync(SecurityAuditActions.KnowledgeSourceProfileDeleted, scopeRef, name, cancellationToken);
    }

    public static void Validate(KnowledgeSourceProfileResource resource)
    {
        if (resource.Kind != KnowledgeResourceKinds.KnowledgeSourceProfile
            || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw Error("knowledge_source_profile_identity_invalid", "Invalid Knowledge Source Profile resource envelope.");
        if (resource.ScopeRef is not { Kind: ResourceScopeKind.Workspace })
            throw Error("knowledge_source_profile_scope_invalid", "A Knowledge Source Profile must belong to a Workspace.");
        ValidateToken(resource.Name, "profile name");
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName) || resource.Definition.DisplayName.Length > 200)
            throw Error("knowledge_source_profile_display_name_invalid", "Profile display names must contain 1 to 200 characters.");
        if (resource.Definition.Description?.Length > 2000)
            throw Error("knowledge_source_profile_description_too_long", "Profile descriptions cannot exceed 2000 characters.");
        ValidateVersion(resource.Definition.Version);
        ValidateSchemaShape(resource.Definition.ConfigurationSchema);
        ValidateFlowTarget(resource.Definition.IngestionFlow, "ingestion");
        ValidateFlowTarget(resource.Definition.RetrievalFlow, "retrieval");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in resource.Definition.ToolBindings)
        {
            ValidateToken(binding.Name, "Tool binding name");
            ValidateToken(binding.Capability, "Tool binding capability");
            if (!names.Add(binding.Name)) throw Error("knowledge_source_profile_binding_duplicate", $"Binding '{binding.Name}' is duplicated.");
        }
        foreach (var route in resource.Definition.ToolSetRoutes)
        {
            ValidateToken(route.Name, "ToolSet route name");
            ValidateToken(route.Capability, "ToolSet route capability");
            ValidateVersion(route.Version);
            if (!names.Add(route.Name)) throw Error("knowledge_source_profile_binding_duplicate", $"Binding '{route.Name}' is duplicated.");
        }
        foreach (var storage in resource.Definition.StorageFlows)
        {
            ValidateToken(storage.Role, "storage Flow role");
            ValidateFlowTarget(storage.Flow, $"storage '{storage.Role}'");
        }
    }

    public static IReadOnlyList<string> ValidateConfiguration(JsonElement schema, JsonElement configuration) =>
        KnowledgeSourceConfigurationSchema.Validate(schema, configuration);

    private async Task<ResolvedKnowledgeSourceProfile> ResolveDefinitionAsync(
        KnowledgeSourceProfileResource profile,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(profile);
        var ingestion = await flows.ResolveAsync(scopeRef, profile.Namespace, profile.Definition.IngestionFlow, cancellationToken);
        var retrieval = await flows.ResolveAsync(scopeRef, profile.Namespace, profile.Definition.RetrievalFlow, cancellationToken);
        ValidateContract(ingestion, KnowledgeFlowContracts.Ingestion, "ingestion");
        ValidateContract(retrieval, KnowledgeFlowContracts.Retrieval, "retrieval");
        var storage = new List<ResolvedKnowledgeFlowBinding>();
        foreach (var item in profile.Definition.StorageFlows)
            storage.Add(await flows.ResolveAsync(scopeRef, profile.Namespace, item.Flow, cancellationToken));
        var bindings = new List<PublishedKnowledgeSourceProfileToolBinding>();
        foreach (var item in profile.Definition.ToolBindings)
            bindings.Add(await ResolveToolAsync(scopeRef, profile.Namespace, item.Name, item.Capability, item.Tool, cancellationToken));
        var routes = new List<PublishedKnowledgeSourceProfileToolSetRoute>();
        foreach (var item in profile.Definition.ToolSetRoutes)
        {
            var toolSet = await references.ResolveAsync<ToolSetResource>(item.ToolSet, profile.Namespace,
                ToolResourceKinds.ToolSet, scopeRef, cancellationToken)
                ?? throw Error("knowledge_source_profile_tool_set_not_found", $"ToolSet '{item.ToolSet.Name}' was not found.");
            var version = await toolSets.GetVersionExactAsync(scopeRef, toolSet.Value.Namespace, toolSet.Value.Name,
                item.Version, cancellationToken)
                ?? throw Error("knowledge_source_profile_tool_set_version_not_found",
                    $"Published ToolSet '{toolSet.Value.Address}:{item.Version}' was not found.");
            var selected = await toolSets.ResolveRouteExactAsync(scopeRef, toolSet.Value.Namespace, toolSet.Value.Name,
                item.Version, item.Capability, item.Route, cancellationToken);
            var selectedReference = new ResourceReference(selected.Member.ToolName, @namespace: selected.Member.ToolNamespace);
            routes.Add(new PublishedKnowledgeSourceProfileToolSetRoute
            {
                Name = item.Name,
                ToolSetName = toolSet.Value.Name,
                ToolSetNamespace = toolSet.Value.Namespace,
                ToolSetUid = toolSet.Value.Uid,
                Version = version.Value.Version,
                DefinitionHash = version.Value.DefinitionHash,
                Capability = item.Capability,
                Route = item.Route,
                SelectedTool = await ResolveToolAsync(scopeRef, profile.Namespace, item.Name, item.Capability,
                    selectedReference, cancellationToken)
            });
        }
        return new ResolvedKnowledgeSourceProfile
        {
            Name = profile.Name,
            Namespace = profile.Namespace,
            Uid = profile.Uid,
            Generation = profile.Generation,
            Version = profile.Definition.Version,
            DefinitionHash = string.Empty,
            ConfigurationSchema = profile.Definition.ConfigurationSchema.Clone(),
            IngestionFlow = ingestion,
            RetrievalFlow = retrieval,
            StorageFlows = storage,
            ToolBindings = bindings,
            ToolSetRoutes = routes,
            Limits = Clone(profile.Definition.Limits),
            Policies = Clone(profile.Definition.Policies),
            AppliedFrom = profile.Definition.AppliedFrom
        };
    }

    private async Task<PublishedKnowledgeSourceProfileToolBinding> ResolveToolAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        string name,
        string capability,
        ResourceReference reference,
        CancellationToken cancellationToken)
    {
        var tool = await references.ResolveAsync<ToolResource>(reference, ownerNamespace, ToolResourceKinds.Tool,
            scopeRef, cancellationToken)
            ?? throw Error("knowledge_source_profile_tool_not_found", $"Tool '{reference.Name}' was not found.");
        if (!tool.Value.Definition.Enabled || tool.Value.Definition.Discovery?.Available != true)
            throw Error("knowledge_source_profile_tool_unavailable", $"Tool '{tool.Value.Address}' is unavailable.");
        var providerReference = tool.Value.Definition.Provider
            ?? throw Error("knowledge_source_profile_tool_provider_missing", $"Tool '{tool.Value.Address}' has no provider.");
        var provider = await references.ResolveAsync<ToolProviderResource>(providerReference, tool.Value.Namespace,
            ToolResourceKinds.ToolProvider, scopeRef, cancellationToken)
            ?? throw Error("knowledge_source_profile_tool_provider_missing", $"Provider for Tool '{tool.Value.Address}' was not found.");
        return new PublishedKnowledgeSourceProfileToolBinding
        {
            Name = name,
            Capability = capability,
            ToolName = tool.Value.Name,
            ToolNamespace = tool.Value.Namespace,
            ToolUid = tool.Value.Uid,
            ToolGeneration = tool.Value.Generation,
            ProviderName = provider.Value.Name,
            ProviderNamespace = provider.Value.Namespace,
            ProviderUid = provider.Value.Uid,
            ProviderGeneration = provider.Value.Generation,
            ExternalToolId = tool.Value.Definition.ExternalId
                ?? throw Error("knowledge_source_profile_tool_external_id_missing", $"Tool '{tool.Value.Address}' has no external identity.")
        };
    }

    private async Task<StoredResource<KnowledgeSourceProfileResource>> ActivateCoreAsync(
        StoredResource<KnowledgeSourceProfileResource> profile,
        StoredResource<KnowledgeSourceProfileRevisionResource> revision,
        JsonElement defaults,
        CancellationToken cancellationToken)
    {
        var impacts = await SourceImpactsAsync(profile.Value, revision.Value.Resolution.ConfigurationSchema,
            defaults, cancellationToken);
        ThrowIfIncompatible(impacts);
        var scopeRef = RequireScope(profile.Value);
        await ApplyDefaultsAsync(profile.Value, revision.Value.Resolution.ConfigurationSchema, defaults, cancellationToken);
        var latest = await GetAsync(profile.Value.Namespace, profile.Value.Name, cancellationToken)
            ?? throw NotFound(profile.Value.Namespace, profile.Value.Name);
        var activated = latest.Value with
        {
            ActiveVersion = revision.Value.Version,
            Generation = checked(latest.Value.Generation + 1),
            Status = Status("ActiveRevisionReady", $"Profile revision '{revision.Value.Version}' is active.")
        };
        var stored = await scopeOperations.WriteAsync(latest.Value, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, activated, latest.ETag, false, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.KnowledgeSourceProfileRevisionActivated, scopeRef,
            $"{profile.Value.Name}:{revision.Value.Version}", cancellationToken);
        return stored;
    }

    private async Task<KnowledgeSourceProfileApplicationPlan> BuildApplicationPlanAsync(
        KnowledgeSourceProfileResource target,
        KnowledgeSourceProfileRevisionResource source,
        PreviewKnowledgeSourceProfileApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var dependencies = new List<KnowledgeSourceProfileDependencyChange>
        {
            Change("ingestion", target.ActiveVersion is null ? null : target.Definition.IngestionFlow.Name,
                FlowIdentity(source.Resolution.IngestionFlow)),
            Change("retrieval", target.ActiveVersion is null ? null : target.Definition.RetrievalFlow.Name,
                FlowIdentity(source.Resolution.RetrievalFlow))
        };
        dependencies.AddRange(source.Resolution.StorageFlows.Select((flow, index) =>
            Change($"storage:{index}", null, FlowIdentity(flow))));
        dependencies.AddRange(source.Resolution.ToolBindings.Select(tool =>
            Change($"tool:{tool.Name}", null, $"{tool.ToolNamespace}/{tool.ToolName}@{tool.ToolGeneration}")));
        dependencies.AddRange(source.Resolution.ToolSetRoutes.Select(route =>
            Change($"toolSet:{route.Name}", null, $"{route.ToolSetNamespace}/{route.ToolSetName}:{route.Version}")));
        var sources = await SourceImpactsAsync(target, source.Resolution.ConfigurationSchema,
            request.ConfigurationDefaults, cancellationToken);
        var issues = sources.Where(value => !value.Compatible)
            .SelectMany(value => value.Issues.Select(issue => $"{value.SourceNamespace}/{value.SourceName}: {issue}"))
            .ToArray();
        return new KnowledgeSourceProfileApplicationPlan
        {
            TargetProfileName = target.Name,
            TargetProfileNamespace = target.Namespace,
            SourceProfileName = source.ProfileName,
            SourceProfileNamespace = source.Namespace,
            SourceVersion = source.Version,
            TargetVersion = request.TargetVersion,
            SourceDefinitionHash = source.DefinitionHash,
            Ready = issues.Length == 0 && dependencies.All(value => value.Available),
            DependencyChanges = dependencies,
            Sources = sources,
            Issues = issues
        };
    }

    private async Task<IReadOnlyList<KnowledgeSourceProfileSourceImpact>> SourceImpactsAsync(
        KnowledgeSourceProfileResource profile,
        JsonElement schema,
        JsonElement defaults,
        CancellationToken cancellationToken)
    {
        var sources = await ReferencingSourcesAsync(profile, cancellationToken);
        return sources.Select(value =>
        {
            var (configuration, applied) = KnowledgeSourceConfigurationSchema.ApplyDefaults(
                value.Value.Definition.AcquisitionConfiguration, defaults);
            var issues = KnowledgeSourceConfigurationSchema.Validate(schema, configuration);
            return new KnowledgeSourceProfileSourceImpact
            {
                SourceName = value.Value.Name,
                SourceNamespace = value.Value.Namespace,
                Compatible = issues.Count == 0,
                Issues = issues,
                AppliedDefaults = applied
            };
        }).ToArray();
    }

    private async Task ApplyDefaultsAsync(
        KnowledgeSourceProfileResource profile,
        JsonElement schema,
        JsonElement defaults,
        CancellationToken cancellationToken)
    {
        foreach (var source in await ReferencingSourcesAsync(profile, cancellationToken))
        {
            var (configuration, applied) = KnowledgeSourceConfigurationSchema.ApplyDefaults(
                source.Value.Definition.AcquisitionConfiguration, defaults);
            if (applied.Count == 0) continue;
            var issues = KnowledgeSourceConfigurationSchema.Validate(schema, configuration);
            if (issues.Count != 0) throw Error("knowledge_source_profile_configuration_incompatible", string.Join(' ', issues));
            var updated = source.Value with
            {
                Definition = source.Value.Definition with { AcquisitionConfiguration = configuration },
                Generation = checked(source.Value.Generation + 1)
            };
            _ = await store.PutExactAsync(RequireScope(source.Value), updated, source.ETag, false, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<StoredResource<KnowledgeSourceResource>>> ReferencingSourcesAsync(
        KnowledgeSourceProfileResource profile,
        CancellationToken cancellationToken) =>
        (await store.ListExactAsync<KnowledgeSourceResource>(RequireScope(profile), KnowledgeResourceKinds.KnowledgeSource,
            0, MaximumProfilesPerWorkspace, cancellationToken))
        .Where(value => References(value.Value, profile))
        .ToArray();

    private static bool References(KnowledgeSourceResource source, KnowledgeSourceProfileResource profile)
    {
        var reference = source.Definition.Profile;
        return reference is not null
            && string.Equals(reference.Name, profile.Name, StringComparison.Ordinal)
            && (reference.Namespace ?? source.Namespace) == profile.Namespace
            && (reference.ScopeRef is null || reference.ScopeRef == profile.ScopeRef);
    }

    private static void ThrowIfIncompatible(IReadOnlyList<KnowledgeSourceProfileSourceImpact> impacts)
    {
        var incompatible = impacts.Where(value => !value.Compatible).ToArray();
        if (incompatible.Length == 0) return;
        throw Error("knowledge_source_profile_configuration_incompatible",
            $"{incompatible.Length} Knowledge Source configuration(s) are incompatible: "
            + string.Join("; ", incompatible.Select(value => $"{value.SourceNamespace}/{value.SourceName}: {string.Join(", ", value.Issues)}")));
    }

    private static KnowledgeSourceProfileDependencyChange Change(string role, string? previous, string? proposed) => new()
    {
        Role = role,
        Previous = previous,
        Proposed = proposed,
        Available = proposed is not null
    };

    private static string FlowIdentity(ResolvedKnowledgeFlowBinding flow) => $"{flow.Namespace}/{flow.Name}:{flow.Version}";

    private static void ValidateContract(ResolvedKnowledgeFlowBinding flow, string contract, string role)
    {
        if (!string.Equals(flow.Contract, contract, StringComparison.Ordinal))
            throw Error($"knowledge_source_profile_{role}_contract_invalid",
                $"The {role} Flow must declare contract '{contract}'.");
    }

    private static void ValidateFlowTarget(KnowledgeFlowTarget? target, string role)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.Name))
            throw Error("knowledge_source_profile_flow_required", $"The {role} Flow is required.");
        if ((target.UseActiveVersion && !string.IsNullOrWhiteSpace(target.Version))
            || (!target.UseActiveVersion && string.IsNullOrWhiteSpace(target.Version)))
            throw Error("knowledge_source_profile_flow_reference_invalid",
                $"Select either the active {role} Flow version or one exact version.");
    }

    private static void ValidateSchemaShape(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw Error("knowledge_source_profile_schema_invalid", "The configuration schema must be a JSON object.");
        if (Encoding.UTF8.GetByteCount(schema.GetRawText()) > MaximumConfigurationSchemaBytes)
            throw Error("knowledge_source_profile_schema_too_large",
                $"The configuration schema cannot exceed {MaximumConfigurationSchemaBytes} bytes.");
        if (schema.TryGetProperty("type", out var type)
            && (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "object", StringComparison.Ordinal)))
            throw Error("knowledge_source_profile_schema_root_invalid", "The configuration schema root type must be 'object'.");
    }

    private static void ValidateDefaults(JsonElement defaults)
    {
        if (defaults.ValueKind != JsonValueKind.Object)
            throw Error("knowledge_source_profile_defaults_invalid", "Configuration defaults must be a JSON object.");
    }

    private static void ValidateToken(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw Error("knowledge_source_profile_token_invalid",
                $"The {label} must contain only letters, digits, '.', '-' or '_' and be at most 128 characters.");
    }

    private static void ValidateVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-'))
            throw Error("knowledge_source_profile_version_invalid",
                "Profile versions must contain only letters, digits, '.' or '-' and be at most 64 characters.");
    }

    private static string DefinitionHash(
        KnowledgeSourceProfileProperties definition,
        ResolvedKnowledgeSourceProfile resolution)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            definition,
            resolution.IngestionFlow,
            resolution.RetrievalFlow,
            resolution.StorageFlows,
            resolution.ToolBindings,
            resolution.ToolSetRoutes
        });
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    private static IReadOnlyDictionary<string, JsonElement> Clone(IReadOnlyDictionary<string, JsonElement> source) =>
        source.ToDictionary(value => value.Key, value => value.Value.Clone(), StringComparer.Ordinal);

    private static string RevisionName(string name, string version)
    {
        var identity = Encoding.UTF8.GetBytes($"{name}\0{version}");
        return $"profile-revision-{Convert.ToHexStringLower(SHA256.HashData(identity))[..32]}";
    }
    private static ResourceScopeRef RequireScope(Resource resource) => resource.ScopeRef
        ?? throw Error("knowledge_source_profile_scope_invalid", "A Knowledge Source Profile requires an ownership scope.");
    private static ScopedResourceAddress Scoped(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string kind, string name) =>
        ScopedResourceAddress.Create(scopeRef, @namespace, kind, name);
    private static ResourceNotFoundException NotFound(ResourceNamespace @namespace, string name) =>
        new(new ResourceKey(KnowledgeResourceKinds.KnowledgeSourceProfile, name, @namespace));
    private static KnowledgeSourceProfileValidationException Error(string code, string message) => new(code, message);
    private static ResourceStatus Status(string reason, string message) => new()
    {
        ProvisioningState = ProvisioningState.Succeeded,
        Conditions = [new ResourceCondition { Type = "Ready", Status = "True", Reason = reason, Message = message }]
    };

    private Task AuditAsync(string action, ResourceScopeRef scopeRef, string reason, CancellationToken cancellationToken)
    {
        var context = requestContext.IsInitialized ? requestContext.Current : null;
        return audit.WriteAsync(new(action, ActorPrincipalId: context?.PrincipalId, TenantId: context?.TenantId,
            WorkspaceId: scopeRef.TargetId, ReasonCode: AuditReason(reason)), cancellationToken);
    }

    private static string AuditReason(string value) => value.Length <= 64
        ? value
        : $"profile:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}"[..64];
}

internal static class KnowledgeSourceConfigurationSchema
{
    public static IReadOnlyList<string> Validate(JsonElement schema, JsonElement value)
    {
        var issues = new List<string>();
        ValidateNode(schema, value, "$", issues);
        return issues;
    }

    public static (JsonElement Configuration, IReadOnlyList<string> Applied) ApplyDefaults(
        JsonElement configuration,
        JsonElement defaults)
    {
        var target = JsonNode.Parse(configuration.GetRawText())?.AsObject() ?? new JsonObject();
        var additions = new List<string>();
        MergeMissing(target, JsonNode.Parse(defaults.GetRawText())?.AsObject() ?? new JsonObject(), "$", additions);
        return (JsonSerializer.SerializeToElement(target), additions);
    }

    private static void MergeMissing(JsonObject target, JsonObject defaults, string path, ICollection<string> additions)
    {
        foreach (var pair in defaults)
        {
            var childPath = $"{path}.{pair.Key}";
            if (!target.TryGetPropertyValue(pair.Key, out var current) || current is null)
            {
                target[pair.Key] = pair.Value?.DeepClone();
                additions.Add(childPath);
            }
            else if (current is JsonObject targetObject && pair.Value is JsonObject defaultObject)
                MergeMissing(targetObject, defaultObject, childPath, additions);
        }
    }

    private static void ValidateNode(JsonElement schema, JsonElement value, string path, ICollection<string> issues)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && !MatchesType(type.GetString(), value))
        {
            issues.Add($"{path} must be of type '{type.GetString()}'.");
            return;
        }
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
            && !values.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
            issues.Add($"{path} is not one of the allowed values.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var propertySchema)
                && propertySchema.ValueKind == JsonValueKind.Object ? propertySchema : default;
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
                {
                    var name = item.GetString()!;
                    if (!value.TryGetProperty(name, out _)) issues.Add($"{path}.{name} is required.");
                }
            }
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var childSchema))
                    ValidateNode(childSchema, property.Value, $"{path}.{property.Name}", issues);
                else if (schema.TryGetProperty("additionalProperties", out var additional)
                    && additional.ValueKind == JsonValueKind.False)
                    issues.Add($"{path}.{property.Name} is not allowed.");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) ValidateNode(itemSchema, item, $"{path}[{index++}]", issues);
        }
    }

    private static bool MatchesType(string? type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };
}
