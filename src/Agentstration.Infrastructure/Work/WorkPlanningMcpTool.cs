using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using static Agentstration.Work.Contracts.WorkPlanContractMapping;
using static Agentstration.Work.Contracts.WorkProjectContractMapping;

namespace Agentstration.Infrastructure.Work;

public enum WorkPlanningOperation { Projects, Project, Task, CreateTasks, Outcome, Get, Propose, Revise, Validate, Activate, Start, Advance, TaskProvider, TaskOperation, BeginTaskOperation, ResumeTaskOperation }

public sealed class WorkPlanningMcpTool : IInternalMcpToolHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly WorkPlanningOperation operation;
    private readonly WorkProjectService projects;
    private readonly WorkTaskTreeService tasks;
    private readonly WorkPlanService plans;
    private readonly WorkPlanExecutionService execution;
    private readonly WorkTaskProviderService providers;
    private readonly ICurrentRequestContext current;
    private readonly IAuthorizationService authorization;

    public WorkPlanningMcpTool(WorkPlanningOperation operation, WorkProjectService projects, WorkTaskTreeService tasks,
        WorkPlanService plans, WorkPlanExecutionService execution, ICurrentRequestContext current, IAuthorizationService authorization, WorkTaskProviderService providers)
    {
        this.operation = operation;
        this.projects = projects;
        this.tasks = tasks;
        this.plans = plans;
        this.execution = execution;
        this.providers = providers;
        this.current = current;
        this.authorization = authorization;
        Definition = Describe(operation);
    }

    public InternalMcpToolDefinition Definition { get; }

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var trusted = current.Current;
        if (trusted.TenantId == Guid.Empty || trusted.WorkspaceId == Guid.Empty || trusted.PrincipalId == Guid.Empty
            || trusted.TenantId != invocation.TenantId || trusted.WorkspaceId != invocation.WorkspaceId.Value
            || trusted.PrincipalId != invocation.PrincipalId)
            throw Error("work_planning_scope_required");
        var permission = operation is WorkPlanningOperation.Projects or WorkPlanningOperation.Project or WorkPlanningOperation.Task or WorkPlanningOperation.Get or WorkPlanningOperation.TaskOperation
            ? AuthorizationPermissions.RunsRead : AuthorizationPermissions.RunsExecute;
        if (!await authorization.HasPermissionAsync(trusted, permission, cancellationToken)) throw Error("authorization_denied");
        if (invocation.CallerKind == ToolDefinitionCallerKind.Agent
            && invocation.ExposedTools?.Any(reference => reference.Name == AgentstrationToolProvider.ToolResourceName(Definition.Name)
                && (reference.Namespace ?? ResourceNamespace.Default).IsDefault && reference.ScopeRef == ResourceScopeRef.Workspace(trusted.WorkspaceId)) != true)
            throw Error("tool_not_authorized");
        if (string.IsNullOrWhiteSpace(invocation.CallId) || invocation.CallId.Length > 256
            || invocation.Arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(invocation.Arguments.GetRawText()) > 512 * 1024)
            throw Error("work_planning_arguments_invalid");
        try
        {
            var properties = Definition.InputSchema.GetProperty("properties");
            foreach (var property in invocation.Arguments.EnumerateObject())
                if (!properties.TryGetProperty(property.Name, out _)) throw Error("work_planning_argument_unknown");
            foreach (var required in Definition.InputSchema.GetProperty("required").EnumerateArray())
                if (!invocation.Arguments.TryGetProperty(required.GetString()!, out _)) throw Error("work_planning_argument_required");
            ToolInputSchemaValidator.Validate(Definition.InputSchema, invocation.Arguments);
            var arguments = invocation.Arguments;
            var workspace = invocation.WorkspaceId;
            var commandId = CommandId(invocation.TenantId, invocation.WorkspaceId, invocation.PrincipalId, Definition.Name, invocation.CallId);
            var planId = arguments.TryGetProperty("planId", out _) ? new WorkPlanId(Read<Guid>(arguments, "planId")) : default;
            var version = arguments.TryGetProperty("expectedVersion", out _) ? Read<long>(arguments, "expectedVersion") : 0;
            if (arguments.TryGetProperty(ToolDryRunContract.ParameterName, out var dry) && dry.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Error("work_planning_arguments_invalid");
            var dryRun = dry.ValueKind == JsonValueKind.True;
            WorkPlanDefinition? definition = null;
            WorkTaskTreeDefinition? tree = null;
            if (operation is WorkPlanningOperation.Propose or WorkPlanningOperation.Revise)
            {
                definition = ToPlanDefinition(Read<WorkPlanDefinitionRequest>(arguments, "definition"));
                _ = Value(definition.Validate());
            }
            if (operation == WorkPlanningOperation.CreateTasks)
            {
                var request = Read<CreateWorkTaskTreeRequest>(arguments, "tree");
                tree = new(new(request.RootTaskId), request.Tasks?.Select(task => task is null ? null! : new WorkTaskDefinition(new(task.Id), task.Kind,
                    task.Title, task.Instruction, task.ParentTaskId is { } parent ? new(parent) : null, task.Description, task.IsOptional)).ToArray() ?? []);
                _ = Value(tree.Validate());
            }
            if (operation == WorkPlanningOperation.BeginTaskOperation)
                _ = Value(Read<ExternalTaskOperationInput>(arguments, "input").Validate());
            // A preview validates structure only; lifecycle and resource authority are checked on execution.
            if (dryRun && operation is not (WorkPlanningOperation.Projects or WorkPlanningOperation.Project or WorkPlanningOperation.Task or WorkPlanningOperation.Get or WorkPlanningOperation.TaskOperation))
                return JsonSerializer.SerializeToElement(new { dryRun = true, structurallyValid = true, requiresExecutionValidation = true, commandId }, Json);
            object value = operation switch
            {
                WorkPlanningOperation.TaskProvider => Value(await providers.DiscoverAsync(workspace, new(Read<Guid>(arguments, "projectId")),
                    Read<string>(arguments, "bindingName"), Read<ExternalTaskOperationKind>(arguments, "operation"), cancellationToken)),
                WorkPlanningOperation.TaskOperation => Value(await providers.GetAsync(workspace, new(Read<Guid>(arguments, "projectId")),
                    new(Read<Guid>(arguments, "operationId")), cancellationToken)),
                WorkPlanningOperation.BeginTaskOperation => Value(await providers.BeginAsync(workspace, new(Read<Guid>(arguments, "projectId")),
                    new(new(commandId), Read<long>(arguments, "expectedProjectVersion"), Read<string>(arguments, "bindingName"),
                        Read<ExternalTaskOperationInput>(arguments, "input"), arguments.TryGetProperty("planId", out _) ? planId : null,
                        arguments.TryGetProperty("planRevision", out _) ? Read<long>(arguments, "planRevision") : null,
                        arguments.TryGetProperty("planDigest", out _) ? Read<string>(arguments, "planDigest") : null), cancellationToken)),
                WorkPlanningOperation.ResumeTaskOperation => Value(await providers.ResumeAsync(workspace, new(Read<Guid>(arguments, "projectId")),
                    new(Read<Guid>(arguments, "operationId")), cancellationToken)),
                WorkPlanningOperation.Projects => ProjectPage(Value(await projects.ListAsync(workspace, OptionalInt(arguments, "skip", 0), OptionalInt(arguments, "take", 25), cancellationToken)), arguments),
                WorkPlanningOperation.Project => ToProjectResponse(Value(await projects.GetAsync(workspace, new(Read<Guid>(arguments, "projectId")), cancellationToken))),
                WorkPlanningOperation.Task => Value(await tasks.GetAsync(workspace, new(Read<Guid>(arguments, "taskId")), cancellationToken)),
                WorkPlanningOperation.CreateTasks => Value(await tasks.CreateAsync(workspace, tree!, cancellationToken)),
                WorkPlanningOperation.Outcome => Value(await tasks.RecordOutcomeAsync(workspace, new(Read<Guid>(arguments, "taskId")),
                    new(commandId, version, Read<WorkTaskOutcome>(arguments, "outcome"), Read<string>(arguments, "evidence")), cancellationToken)),
                WorkPlanningOperation.Get => ToPlanResponse(Value(await plans.GetAsync(workspace, planId, cancellationToken))),
                WorkPlanningOperation.Propose => ToPlanResponse(Value(await plans.CreateAsync(workspace, planId, new(commandId, 0, definition!), cancellationToken))),
                WorkPlanningOperation.Revise => ToPlanResponse(Value(await plans.ReviseAsync(workspace, planId, new(commandId, version, definition!), cancellationToken))),
                WorkPlanningOperation.Validate or WorkPlanningOperation.Activate => ToPlanResponse(Value(await plans.TransitionAsync(workspace, planId,
                    new(commandId, version, Read<long>(arguments, "revision"), Read<string>(arguments, "revisionDigest"),
                        operation == WorkPlanningOperation.Validate ? WorkPlanAction.Validate : WorkPlanAction.Activate), cancellationToken))),
                WorkPlanningOperation.Start => ToPlanResponse(Value(await execution.StartAsync(workspace, planId,
                    new(commandId, version, Read<long>(arguments, "revision"), Read<string>(arguments, "revisionDigest"),
                        Read<WorkPlanExecutionTargetRequest[]>(arguments, "targets").Select(target => target is null ? null! : new WorkPlanTaskTarget(new(target.TaskId), target.Flow, target.Input) { Connections = target.Connections }).ToArray())
                    { ProjectId = arguments.TryGetProperty("projectId", out _) ? new WorkProjectId(Read<Guid>(arguments, "projectId")) : null }, cancellationToken))),
                WorkPlanningOperation.Advance => ToPlanResponse(Value(await execution.AdvanceAsync(workspace, planId, cancellationToken))),
                _ => throw new InvalidOperationException("Unsupported Work planning operation.")
            };
            return JsonSerializer.SerializeToElement(new { commandId, value }, Json);
        }
        catch (FlowValidationException exception) { throw Error(exception.Code); }
        catch (FlowConcurrencyException) { throw Error("flow_run_conflict"); }
        catch (ToolInputValidationException exception) { throw Error(exception.Code); }
        catch (JsonException) { throw Error("work_planning_arguments_invalid"); }
        catch (ArgumentException) { throw Error("work_planning_arguments_invalid"); }

    }

    internal static Guid CommandId(Guid tenantId, WorkspaceId workspaceId, Guid principalId, string tool, string callId) =>
        new(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { TenantId = tenantId, WorkspaceId = workspaceId, PrincipalId = principalId, Tool = tool, CallId = callId })).AsSpan(0, 16));

    private static WorkProjectPageResponse ProjectPage(WorkProjectPage page, JsonElement arguments) =>
        new(page.Items.Select(ToProjectResponse).ToArray(), page.HasMore, OptionalInt(arguments, "skip", 0), OptionalInt(arguments, "take", 25));

    private static T Value<T>(Result<T> result) where T : class => result.IsSuccess ? result.Value! : throw Error(result.ErrorCode!);
    private static T Read<T>(JsonElement arguments, string name) => arguments.TryGetProperty(name, out var value)
        ? value.Deserialize<T>(Json) ?? throw Error("work_planning_argument_required") : throw Error("work_planning_argument_required");
    private static int OptionalInt(JsonElement arguments, string name, int fallback) => arguments.TryGetProperty(name, out _) ? Read<int>(arguments, name) : fallback;
    private static ToolDefinitionInvocationException Error(string code) => new(code, $"Work planning failed: {code}.");

    internal static InternalMcpToolDefinition Describe(WorkPlanningOperation operation)
    {
        var properties = new JsonObject();
        var required = new List<string>();
        void Add<T>(string name, bool optional = false)
        {
            properties[name] = Json.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
            {
                TransformSchemaNode = (_, node) => node is JsonValue value && value.TryGetValue<bool>(out var allowed) && allowed
                    ? new JsonObject() : node
            });
            if (!optional) required.Add(name);
        }
        string name;
        switch (operation)
        {
            case WorkPlanningOperation.TaskProvider:
                name = "work.tracking.describe"; Add<Guid>("projectId"); Add<string>("bindingName"); Add<ExternalTaskOperationKind>("operation"); break;
            case WorkPlanningOperation.TaskOperation:
                name = "work.tracking.get"; Add<Guid>("projectId"); Add<Guid>("operationId"); break;
            case WorkPlanningOperation.BeginTaskOperation:
                name = "work.tracking.begin"; Add<Guid>("projectId"); Add<long>("expectedProjectVersion"); Add<string>("bindingName");
                Add<ExternalTaskOperationInput>("input"); Add<Guid>("planId", true); Add<long>("planRevision", true); Add<string>("planDigest", true); break;
            case WorkPlanningOperation.ResumeTaskOperation:
                name = "work.tracking.resume"; Add<Guid>("projectId"); Add<Guid>("operationId"); break;
            case WorkPlanningOperation.Projects: name = "work.project.list"; Add<int>("skip", true); Add<int>("take", true); break;
            case WorkPlanningOperation.Project: name = "work.project.get"; Add<Guid>("projectId"); break;
            case WorkPlanningOperation.Task: name = "work.task.get"; Add<Guid>("taskId"); break;
            case WorkPlanningOperation.CreateTasks: name = "work.task.create-tree"; Add<CreateWorkTaskTreeRequest>("tree"); break;
            case WorkPlanningOperation.Outcome:
                name = "work.task.record-outcome"; Add<Guid>("taskId"); Add<long>("expectedVersion"); Add<WorkTaskOutcome>("outcome"); Add<string>("evidence"); break;
            default:
                name = "work.plan." + operation.ToString().ToLowerInvariant();
                Add<Guid>("planId");
                if (operation is not (WorkPlanningOperation.Get or WorkPlanningOperation.Propose or WorkPlanningOperation.Advance)) Add<long>("expectedVersion");
                if (operation is WorkPlanningOperation.Propose or WorkPlanningOperation.Revise) Add<WorkPlanDefinitionRequest>("definition");
                if (operation is WorkPlanningOperation.Validate or WorkPlanningOperation.Activate or WorkPlanningOperation.Start)
                { Add<long>("revision"); Add<string>("revisionDigest"); }
                if (operation == WorkPlanningOperation.Start) { Add<WorkPlanExecutionTargetRequest[]>("targets"); Add<Guid>("projectId", true); }
                break;
        }
        Add<bool>(ToolDryRunContract.ParameterName, true);
        return new(name, name, "Governed local Work planning in the authenticated Workspace and Principal. Uses canonical Work services, including optional capability-described tracking connections; no template required. Mutations replay by trusted Tool call identity. Dry runs validate structure without effects.",
            JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false }, Json),
            JsonSerializer.SerializeToElement(new { type = "object" }, Json),
            InitialCategory: new("work-planning", "Work planning", "Local Project, task and WorkPlan operations."));
    }
}

internal sealed class WorkPlanningToolDefinition(WorkPlanningOperation operation) : IInternalMcpToolDefinitionProvider
{
    public InternalMcpToolDefinition Definition { get; } = WorkPlanningMcpTool.Describe(operation);
}
