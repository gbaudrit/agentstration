using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Infrastructure.Declarative;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Infrastructure.Bootstrap;

internal static class WorkspaceBootstrapResource
{
    public const string ActiveFlowPlanningKind = "Flow:Active";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static T Parse<T>(BootstrapResourceDocument resource) =>
        ResourceManifestSerializer.FromJson<T>(JsonSerializer.Serialize(resource, JsonOptions));

    public static string PlanningParent(ResourceNamespace @namespace) => @namespace.ToString();

    public static BootstrapResourcePlanResult Created(
        BootstrapPlanningContext planning,
        string kind,
        string name,
        ResourceNamespace @namespace)
    {
        planning.Register(kind, name, PlanningParent(@namespace));
        return new(BootstrapResourceDisposition.Create);
    }

    public static bool IsAvailable(
        BootstrapPlanningContext planning,
        string kind,
        string name,
        ResourceNamespace @namespace) =>
        planning.Contains(kind, name, PlanningParent(@namespace));

    public static WorkspaceId Workspace(BootstrapResourceOperationContext operation) =>
        new(operation.Target?.WorkspaceId
            ?? throw new InvalidOperationException("A Workspace bootstrap resource requires an explicit Workspace target."));
}

public sealed class ModelProviderBootstrapResourceHandler(ModelProviderManagementService service) : IBootstrapResourceHandler
{
    public string Kind => ResourceKinds.ModelProvider;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<ModelProviderResource>(resource);
        if (await service.GetAsync(value.Namespace, value.Name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        await service.ValidateForCreateAsync(value, cancellationToken);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<ModelProviderResource>(resource);
        if (await service.GetAsync(value.Namespace, value.Name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        _ = await service.CreateAsync(value, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class RuntimeProfileBootstrapResourceHandler(RuntimeProfileManagementService service) : IBootstrapResourceHandler
{
    public string Kind => ResourceKinds.RuntimeProfile;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<RuntimeProfileResource>(resource);
        if (await service.GetAsync(value.Namespace, value.Name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        service.ValidateForCreate(value);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<RuntimeProfileResource>(resource);
        if (await service.GetAsync(value.Namespace, value.Name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        _ = await service.CreateAsync(value, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class ModelProfileBootstrapResourceHandler(
    ModelProfileManagementService service,
    ModelProviderManagementService providers) : IBootstrapResourceHandler
{
    public string Kind => ResourceKinds.ModelProfile;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<ModelProfileResource>(resource);
        if (await service.GetAsync(value.Namespace, value.Name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        ValidateBasic(value);
        var provider = value.Definition.Provider.Resolve(value.Namespace, ResourceKinds.ModelProvider);
        var existingProvider = await providers.GetAsync(provider.Namespace, provider.Name, cancellationToken);
        if (existingProvider is null
            && !WorkspaceBootstrapResource.IsAvailable(planning, provider.Kind, provider.Name, provider.Namespace))
            throw new InvalidOperationException($"Referenced model provider '{provider}' does not exist and was not planned earlier.");
        if (existingProvider is not null)
            await service.ValidateForCreateAsync(value, cancellationToken);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<ModelProfileResource>(resource);
        if (await service.GetAsync(value.Namespace, value.Name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        _ = await service.CreateAsync(value, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    private static void ValidateBasic(ModelProfileResource resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.Provider.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.Model.Name);
        if (resource.Definition.Provider.WorkspaceRef is not null)
            throw new InvalidOperationException("Cross-workspace model provider references are not supported.");
        if (resource.Definition.Generation.Temperature is < 0 or > 2)
            throw new InvalidOperationException("Model profile temperature must be between 0 and 2.");
    }
}

public sealed class AgentBootstrapResourceHandler(
    AgentManagementService service,
    ModelProfileManagementService modelProfiles,
    RuntimeProfileManagementService runtimeProfiles) : IBootstrapResourceHandler
{
    public string Kind => ResourceKinds.Agent;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<AgentResource>(resource);
        if (await service.GetAgentAsync(value.Namespace, value.Name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        ValidateBasic(value);
        var model = value.Definition.ModelProfile.Resolve(value.Namespace, ResourceKinds.ModelProfile);
        var runtime = value.Definition.RuntimeProfile.Resolve(value.Namespace, ResourceKinds.RuntimeProfile);
        var modelExists = await modelProfiles.GetAsync(model.Namespace, model.Name, cancellationToken) is not null;
        var runtimeExists = await runtimeProfiles.GetAsync(runtime.Namespace, runtime.Name, cancellationToken) is not null;
        if (!modelExists && !WorkspaceBootstrapResource.IsAvailable(planning, model.Kind, model.Name, model.Namespace))
            throw new InvalidOperationException($"Referenced model profile '{model}' does not exist and was not planned earlier.");
        if (!runtimeExists && !WorkspaceBootstrapResource.IsAvailable(planning, runtime.Kind, runtime.Name, runtime.Namespace))
            throw new InvalidOperationException($"Referenced runtime profile '{runtime}' does not exist and was not planned earlier.");
        if (modelExists && runtimeExists)
            await service.ValidateForCreateAsync(value, cancellationToken);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<AgentResource>(resource);
        if (await service.GetAgentAsync(value.Namespace, value.Name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        _ = await service.PutAgentAsync(value, null, true, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    private static void ValidateBasic(AgentResource resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Definition.Instructions);
        if (resource.Definition.ModelProfile.WorkspaceRef is not null
            || resource.Definition.RuntimeProfile.WorkspaceRef is not null
            || resource.Definition.Tools.Any(tool => tool.WorkspaceRef is not null))
            throw new InvalidOperationException("Cross-workspace Agent references are not supported.");
    }
}

public sealed class FlowBootstrapResourceHandler(
    FlowService service,
    IFlowDefinitionValidator graphValidator,
    TimeProvider timeProvider) : IBootstrapResourceHandler
{
    public string Kind => ResourceKinds.Flow;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<DeclarativeResourceEnvelope<DeclarativeFlowDefinition>>(resource);
        var workspaceId = WorkspaceBootstrapResource.Workspace(operation);
        if (await service.GetAsync(workspaceId, new(value.Metadata.Name, value.Metadata.Namespace), cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        var now = timeProvider.GetUtcNow();
        FlowValidator.Validate(new(
            workspaceId,
            new(value.Metadata.Name, value.Metadata.Namespace),
            value.Metadata.Name,
            value.Definition.Description,
            value.Definition.Version,
            value.Definition.Enabled,
            null,
            value.Definition.Spec,
            value.Definition.Metadata,
            now,
            now,
            value.Definition.DisplayName,
            value.Definition.Graph));
        if (value.Definition.Graph is not null)
        {
            var validation = await graphValidator.ValidateAsync(value.Definition.Graph, new(ResolveResources: false), cancellationToken);
            var error = validation.Issues.FirstOrDefault(issue => issue.Severity == FlowValidationSeverity.Error);
            if (error is not null) throw new FlowValidationException(error.Code, error.Message);
        }
        var result = WorkspaceBootstrapResource.Created(planning, Kind, value.Metadata.Name, value.Metadata.Namespace);
        if (value.Definition.Publish && value.Definition.Activate)
            planning.Register(
                WorkspaceBootstrapResource.ActiveFlowPlanningKind,
                value.Metadata.Name,
                WorkspaceBootstrapResource.PlanningParent(value.Metadata.Namespace));
        return result;
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<DeclarativeResourceEnvelope<DeclarativeFlowDefinition>>(resource);
        var workspaceId = WorkspaceBootstrapResource.Workspace(operation);
        var flowId = new FlowId(value.Metadata.Name, value.Metadata.Namespace);
        if (await service.GetAsync(workspaceId, flowId, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        var definition = value.Definition;
        _ = await service.CreateAsync(workspaceId, new(
            value.Metadata.Name,
            definition.Description,
            definition.Version,
            definition.Enabled,
            definition.Spec,
            definition.Metadata,
            definition.Graph,
            definition.DisplayName), value.Metadata.Namespace, cancellationToken);
        if (definition.Publish)
            _ = await service.PublishVersionAsync(workspaceId, flowId, definition.Version, definition.Activate, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class EntryBootstrapResourceHandler(
    EntryAdministrationService service,
    IWorkplaceRepository repository,
    AgentManagementService agents,
    FlowService flows,
    TimeProvider timeProvider) : IBootstrapResourceHandler
{
    public string Kind => ResourceKinds.Entry;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<DeclarativeResourceEnvelope<DeclarativeEntryDefinition>>(resource);
        var workspaceId = WorkspaceBootstrapResource.Workspace(operation);
        var entryId = new EntryId(value.Metadata.Name, value.Metadata.Namespace);
        if (await repository.GetEntryDraftAsync(workspaceId, entryId, cancellationToken) is not null
            || await repository.GetEntryAsync(workspaceId, entryId, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        var draft = ToDraft(workspaceId, value, timeProvider.GetUtcNow());
        WorkplaceValidation.Validate(draft);
        if (value.Definition.Publish)
            await ValidateTargetAsync(draft, planning, agents, flows, cancellationToken);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Metadata.Name, value.Metadata.Namespace);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation,
        CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<DeclarativeResourceEnvelope<DeclarativeEntryDefinition>>(resource);
        var workspaceId = WorkspaceBootstrapResource.Workspace(operation);
        var entryId = new EntryId(value.Metadata.Name, value.Metadata.Namespace);
        if (await repository.GetEntryDraftAsync(workspaceId, entryId, cancellationToken) is not null
            || await repository.GetEntryAsync(workspaceId, entryId, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        var saved = await service.SaveAsync(ToDraft(workspaceId, value, timeProvider.GetUtcNow()), cancellationToken);
        if (value.Definition.Publish)
            _ = await service.PublishAsync(workspaceId, saved.Id, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    private static EntryDraft ToDraft(
        WorkspaceId workspaceId,
        DeclarativeResourceEnvelope<DeclarativeEntryDefinition> value,
        DateTimeOffset now) => new()
        {
            WorkspaceId = workspaceId,
            Id = new(value.Metadata.Name, value.Metadata.Namespace),
            Name = value.Metadata.Name,
            DisplayName = value.Definition.DisplayName ?? value.Metadata.Name,
            Description = value.Definition.Description,
            Presentation = value.Definition.Presentation,
            Binding = value.Definition.Binding,
            Behavior = value.Definition.Behavior,
            UpdatedAt = now
        };

    private static async Task ValidateTargetAsync(
        EntryDraft draft,
        BootstrapPlanningContext planning,
        AgentManagementService agents,
        FlowService flows,
        CancellationToken cancellationToken)
    {
        var targetNamespace = draft.Binding.Namespace ?? draft.Id.Namespace;
        if (draft.Binding.Kind == EntryBindingKind.Agent)
        {
            var exists = await agents.GetAgentAsync(targetNamespace, draft.Binding.ResourceId, cancellationToken) is not null;
            if (!exists && !WorkspaceBootstrapResource.IsAvailable(
                    planning,
                    ResourceKinds.Agent,
                    draft.Binding.ResourceId,
                    targetNamespace))
                throw new InvalidOperationException(
                    $"Referenced Agent '{targetNamespace}/{draft.Binding.ResourceId}' does not exist and was not planned earlier.");
            return;
        }

        var flow = await flows.GetAsync(draft.WorkspaceId, new(draft.Binding.ResourceId, targetNamespace), cancellationToken);
        var activeFlowPlanned = WorkspaceBootstrapResource.IsAvailable(
            planning,
            WorkspaceBootstrapResource.ActiveFlowPlanningKind,
            draft.Binding.ResourceId,
            targetNamespace);
        if (flow?.Value.ActiveVersion is null && !activeFlowPlanned)
            throw new InvalidOperationException(
                $"Referenced Flow '{targetNamespace}/{draft.Binding.ResourceId}' has no active published version and none was planned earlier.");
    }
}
