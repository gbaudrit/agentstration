using System.Diagnostics;
using System.Text.Json;
using Agentstration.ResourcePlanning;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.ResourcePlanning;

public abstract class ResourcePlanningMcpTool(
    ResourcePlanService plans,
    ResourcePlanMaterializationService materializer,
    ResourceChangeSetService changeSets,
    ResourceChangeSetValidationService validations) : IInternalMcpToolHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public abstract InternalMcpToolDefinition Definition { get; }
    protected abstract ResourcePlanningToolOperation Operation { get; }

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.ValueKind != JsonValueKind.Object)
            throw new ToolDefinitionInvocationException("resource_planning_arguments_invalid", "Resource Planning Tool arguments must be a JSON object.");
        try
        {
            var scope = new ResourcePlanScope(invocation.TenantId, invocation.WorkspaceId);
            return Operation switch
            {
                ResourcePlanningToolOperation.Create => await CreateAsync(scope, invocation, cancellationToken),
                ResourcePlanningToolOperation.Get => await GetAsync(scope, invocation, cancellationToken),
                ResourcePlanningToolOperation.Refine => await RefineAsync(scope, invocation, cancellationToken),
                ResourcePlanningToolOperation.Submit => await SubmitAsync(scope, invocation, cancellationToken),
                ResourcePlanningToolOperation.Materialize => await MaterializeAsync(scope, invocation, cancellationToken),
                ResourcePlanningToolOperation.CreateChangeSet => await CreateChangeSetAsync(scope, invocation, cancellationToken),
                ResourcePlanningToolOperation.Validate => await ValidateAsync(scope, invocation, cancellationToken),
                _ => throw new UnreachableException()
            };
        }
        catch (ToolDefinitionInvocationException) { throw; }
        catch (ResourcePlanNotFoundException exception) { throw Failure("resource_plan_not_found", exception); }
        catch (ResourceChangeSetNotFoundException exception) { throw Failure("resource_change_set_not_found", exception); }
        catch (ResourcePlanConcurrencyException exception) { throw Failure("resource_plan_concurrency", exception); }
        catch (ResourcePlanLifecycleException exception) { throw Failure(exception.Code, exception); }
        catch (ResourcePlanValidationException exception) { throw Failure("resource_plan_content_invalid", exception); }
        catch (ResourcePlanMaterializationException exception) { throw Failure("resource_plan_materialization_failed", exception); }
        catch (ArgumentException exception) { throw Failure("resource_planning_arguments_invalid", exception); }
        catch (JsonException exception) { throw Failure("resource_planning_arguments_invalid", exception); }
    }

    private async Task<JsonElement?> CreateAsync(ResourcePlanScope scope, InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        EnsureOnly(invocation.Arguments, "title", "goal", "description", "plan");
        var functional = Required(invocation.Arguments, "plan").Deserialize<FunctionalResourcePlanV1>(JsonOptions)
            ?? throw new JsonException("The functional plan is required.");
        var stored = await plans.CreateAsync(scope, new(
            RequiredString(invocation.Arguments, "title"),
            RequiredString(invocation.Arguments, "goal"),
            OptionalString(invocation.Arguments, "description"),
            FunctionalResourcePlanSerializer.Serialize(functional),
            FlowRunId: invocation.RunId,
            CallerId: invocation.CallerId,
            CausationId: invocation.CallId,
            CorrelationId: invocation.CorrelationId), invocation.PrincipalId, cancellationToken);
        return Snapshot(stored);
    }

    private async Task<JsonElement?> GetAsync(ResourcePlanScope scope, InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        EnsureOnly(invocation.Arguments, "planId");
        var id = RequiredGuid(invocation.Arguments, "planId");
        var stored = await plans.GetAsync(scope, new(id), cancellationToken) ?? throw new ResourcePlanNotFoundException(new(id));
        return Snapshot(stored);
    }

    private async Task<JsonElement?> RefineAsync(ResourcePlanScope scope, InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        EnsureOnly(invocation.Arguments, "planId", "expectedETag", "title", "goal", "description", "plan");
        var functional = Required(invocation.Arguments, "plan").Deserialize<FunctionalResourcePlanV1>(JsonOptions)
            ?? throw new JsonException("The functional plan is required.");
        var stored = await plans.RefineAsync(scope, new(RequiredGuid(invocation.Arguments, "planId")), new(
            RequiredString(invocation.Arguments, "title"), RequiredString(invocation.Arguments, "goal"),
            OptionalString(invocation.Arguments, "description"), FunctionalResourcePlanSerializer.Serialize(functional)),
            RequiredString(invocation.Arguments, "expectedETag"), invocation.PrincipalId, cancellationToken);
        return Snapshot(stored);
    }

    private async Task<JsonElement?> MaterializeAsync(ResourcePlanScope scope, InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        EnsureOnly(invocation.Arguments, "planId", "bindings");
        return JsonSerializer.SerializeToElement(await materializer.MaterializeAsync(scope, new(RequiredGuid(invocation.Arguments, "planId")), BindingRequest(invocation.Arguments), cancellationToken), JsonOptions);
    }

    private async Task<JsonElement?> SubmitAsync(ResourcePlanScope scope, InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        EnsureOnly(invocation.Arguments, "planId", "expectedETag");
        var stored = await plans.ChangeStatusAsync(scope, new(RequiredGuid(invocation.Arguments, "planId")), new(ResourcePlanStatus.Ready, "Submitted by governed planning Tool."),
            RequiredString(invocation.Arguments, "expectedETag"), invocation.PrincipalId, cancellationToken);
        return Snapshot(stored);
    }

    private async Task<JsonElement?> CreateChangeSetAsync(ResourcePlanScope scope, InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        EnsureOnly(invocation.Arguments, "planId", "bindings", "expectedDigest");
        var stored = await changeSets.CreateAsync(scope, new(RequiredGuid(invocation.Arguments, "planId")), invocation.PrincipalId, BindingRequest(invocation.Arguments), cancellationToken);
        return JsonSerializer.SerializeToElement(new { changeSet = stored.Value, etag = stored.ETag }, JsonOptions);
    }

    private async Task<JsonElement?> ValidateAsync(ResourcePlanScope scope, InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        EnsureOnly(invocation.Arguments, "changeSetId");
        return JsonSerializer.SerializeToElement(await validations.ValidateAsync(scope, new(RequiredGuid(invocation.Arguments, "changeSetId")), invocation.PrincipalId, cancellationToken), JsonOptions);
    }

    private static JsonElement Snapshot(ResourcePlanSnapshot stored) => JsonSerializer.SerializeToElement(new { plan = stored.Value, etag = stored.ETag }, JsonOptions);
    private static ResourcePlanMaterializationRequest BindingRequest(JsonElement arguments) => new(
        arguments.TryGetProperty("bindings", out var bindings) ? bindings.Deserialize<ResourcePlanAgentBinding[]>(JsonOptions) ?? throw new JsonException("Bindings must be an array.") : [],
        OptionalString(arguments, "expectedDigest"));
    private static ToolDefinitionInvocationException Failure(string code, Exception exception) => new(code, exception.Message, exception);
    private static JsonElement Required(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) ? value : throw new ToolDefinitionInvocationException("resource_planning_argument_required", $"Argument '{name}' is required.");
    private static string RequiredString(JsonElement arguments, string name) =>
        Required(arguments, name).ValueKind == JsonValueKind.String && Required(arguments, name).GetString() is { Length: > 0 } value
            ? value : throw new ToolDefinitionInvocationException("resource_planning_argument_invalid", $"Argument '{name}' must be a non-empty string.");
    private static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static Guid RequiredGuid(JsonElement arguments, string name) =>
        Guid.TryParse(RequiredString(arguments, name), out var value) ? value : throw new ToolDefinitionInvocationException("resource_planning_argument_invalid", $"Argument '{name}' must be a UUID.");
    private static void EnsureOnly(JsonElement arguments, params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var argument in arguments.EnumerateObject())
            if (!names.Contains(argument.Name))
                throw new ToolDefinitionInvocationException("resource_planning_argument_unknown", $"Argument '{argument.Name}' is not declared by the Tool schema.");
    }

    protected static InternalMcpToolDefinition Define(string name, string displayName, string description, object properties, string[] required) => new(
        name, displayName, description,
        JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false }),
        JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = true }));

    protected static object StringProperty(string? description = null) => new { type = "string", description };
    protected static object BindingsProperty() => new { type = "array", description = "Explicit Model Profile and Runtime Profile references for each planned role.", items = new { type = "object", properties = new { logicalId = StringProperty(), modelProfile = new { type = "object", properties = new { name = StringProperty(), scopeRef = StringProperty(), @namespace = StringProperty() }, required = new[] { "name" } }, runtimeProfile = new { type = "object", properties = new { name = StringProperty(), scopeRef = StringProperty(), @namespace = StringProperty() }, required = new[] { "name" } } }, required = new[] { "logicalId", "modelProfile", "runtimeProfile" }, additionalProperties = false } };
    protected static object FunctionalPlanProperty() => new
    {
        type = "object",
        description = "Functional solution intent; never a canonical Agentstration Resource manifest.",
        properties = new
        {
            solution = new { type = "object" },
            roles = new { type = "array" },
            workflows = new { type = "array" },
            integrations = new { type = "array" },
            experiences = new { type = "array" },
            dependencies = new { type = "array" },
            runtime = new { type = "object" }
        },
        required = new[] { "solution" },
        additionalProperties = false
    };
}

public enum ResourcePlanningToolOperation { Create, Get, Refine, Submit, Materialize, CreateChangeSet, Validate }

public sealed class ResourcePlanCreateMcpTool(ResourcePlanService plans, ResourcePlanMaterializationService materializer, ResourceChangeSetService changeSets, ResourceChangeSetValidationService validations)
    : ResourcePlanningMcpTool(plans, materializer, changeSets, validations)
{
    public override InternalMcpToolDefinition Definition { get; } = Define(AgentstrationInternalTools.ResourcePlanCreate, "Create Resource Plan", "Creates a draft from functional solution intent in the trusted Workspace.",
        new { title = StringProperty(), goal = StringProperty(), description = StringProperty(), plan = FunctionalPlanProperty() }, ["title", "goal", "plan"]);
    protected override ResourcePlanningToolOperation Operation => ResourcePlanningToolOperation.Create;
}

public sealed class ResourcePlanGetMcpTool(ResourcePlanService plans, ResourcePlanMaterializationService materializer, ResourceChangeSetService changeSets, ResourceChangeSetValidationService validations)
    : ResourcePlanningMcpTool(plans, materializer, changeSets, validations)
{
    public override InternalMcpToolDefinition Definition { get; } = Define(AgentstrationInternalTools.ResourcePlanGet, "Get Resource Plan", "Reads one Resource Plan from the trusted Workspace.", new { planId = StringProperty() }, ["planId"]);
    protected override ResourcePlanningToolOperation Operation => ResourcePlanningToolOperation.Get;
}

public sealed class ResourcePlanRefineMcpTool(ResourcePlanService plans, ResourcePlanMaterializationService materializer, ResourceChangeSetService changeSets, ResourceChangeSetValidationService validations)
    : ResourcePlanningMcpTool(plans, materializer, changeSets, validations)
{
    public override InternalMcpToolDefinition Definition { get; } = Define(AgentstrationInternalTools.ResourcePlanRefine, "Refine Resource Plan", "Atomically refines functional planning elements under optimistic concurrency.",
        new { planId = StringProperty(), expectedETag = StringProperty(), title = StringProperty(), goal = StringProperty(), description = StringProperty(), plan = FunctionalPlanProperty() },
        ["planId", "expectedETag", "title", "goal", "plan"]);
    protected override ResourcePlanningToolOperation Operation => ResourcePlanningToolOperation.Refine;
}

public sealed class ResourcePlanSubmitMcpTool(ResourcePlanService plans, ResourcePlanMaterializationService materializer, ResourceChangeSetService changeSets, ResourceChangeSetValidationService validations)
    : ResourcePlanningMcpTool(plans, materializer, changeSets, validations)
{
    public override InternalMcpToolDefinition Definition { get; } = Define(AgentstrationInternalTools.ResourcePlanSubmit, "Submit Resource Plan", "Moves an explicitly reviewed draft to Ready under optimistic concurrency.", new { planId = StringProperty(), expectedETag = StringProperty() }, ["planId", "expectedETag"]);
    protected override ResourcePlanningToolOperation Operation => ResourcePlanningToolOperation.Submit;
}

public sealed class ResourcePlanMaterializeMcpTool(ResourcePlanService plans, ResourcePlanMaterializationService materializer, ResourceChangeSetService changeSets, ResourceChangeSetValidationService validations)
    : ResourcePlanningMcpTool(plans, materializer, changeSets, validations)
{
    public override InternalMcpToolDefinition Definition { get; } = Define(AgentstrationInternalTools.ResourcePlanMaterialize, "Materialize Resource Plan", "Computes deterministic proposed changes with explicit Agent profile bindings without applying them.", new { planId = StringProperty(), bindings = BindingsProperty() }, ["planId"]);
    protected override ResourcePlanningToolOperation Operation => ResourcePlanningToolOperation.Materialize;
}

public sealed class ResourceChangeSetCreateMcpTool(ResourcePlanService plans, ResourcePlanMaterializationService materializer, ResourceChangeSetService changeSets, ResourceChangeSetValidationService validations)
    : ResourcePlanningMcpTool(plans, materializer, changeSets, validations)
{
    public override InternalMcpToolDefinition Definition { get; } = Define(AgentstrationInternalTools.ResourceChangeSetCreate, "Create Resource ChangeSet", "Creates an idempotent review boundary with explicit Agent profile bindings without applying it.", new { planId = StringProperty(), bindings = BindingsProperty(), expectedDigest = StringProperty("Digest returned by the reviewed materialization.") }, ["planId"]);
    protected override ResourcePlanningToolOperation Operation => ResourcePlanningToolOperation.CreateChangeSet;
}

public sealed class ResourceChangeSetValidateMcpTool(ResourcePlanService plans, ResourcePlanMaterializationService materializer, ResourceChangeSetService changeSets, ResourceChangeSetValidationService validations)
    : ResourcePlanningMcpTool(plans, materializer, changeSets, validations)
{
    public override InternalMcpToolDefinition Definition { get; } = Define(AgentstrationInternalTools.ResourceChangeSetValidate, "Validate Resource ChangeSet", "Validates a pinned reviewable change set without applying it.", new { changeSetId = StringProperty() }, ["changeSetId"]);
    protected override ResourcePlanningToolOperation Operation => ResourcePlanningToolOperation.Validate;
}
