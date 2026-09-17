using System.Text.Json;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.ResourcePlanning.Storage.Abstractions;

namespace Agentstration.ResourcePlanning;

public sealed class ResourcePlanService(
    IResourcePlanRepository repository,
    TimeProvider timeProvider,
    IResourcePlanContentValidator contentValidator)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public Task InitializeAsync(CancellationToken cancellationToken) => repository.InitializeAsync(cancellationToken);

    public async Task<ResourcePlanSnapshot> CreateAsync(
        ResourcePlanScope scope,
        CreateResourcePlanRequest request,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        ValidateActor(actorPrincipalId);
        var now = timeProvider.GetUtcNow();
        var id = ResourcePlanId.New();
        var plan = new ResourcePlan(
            id,
            scope,
            Required(request.Title, nameof(request.Title), 160),
            Required(request.Goal, nameof(request.Goal), 4000),
            Optional(request.Description, 8000),
            ValidateContent(request.Content),
            ResourcePlanStatus.Draft,
            1,
            new(actorPrincipalId, request.WorkItemId, request.FlowRunId, request.CallerId, request.CausationId, request.CorrelationId),
            now,
            now);
        var stored = await repository.CreateAsync(plan, cancellationToken);
        await repository.AddActivityAsync(Activity(plan, ResourcePlanActivityType.Created, actorPrincipalId, null, now), cancellationToken);
        return stored;
    }

    public Task<ResourcePlanSnapshot?> GetAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        return repository.GetAsync(scope, id, cancellationToken);
    }

    public Task<ResourcePlanPage> ListAsync(ResourcePlanScope scope, ResourcePlanStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        return repository.ListAsync(scope, status, skip, Math.Min(take, 200), cancellationToken);
    }

    public async Task<ResourcePlanSnapshot> RefineAsync(
        ResourcePlanScope scope,
        ResourcePlanId id,
        RefineResourcePlanRequest request,
        string expectedETag,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        ValidateActor(actorPrincipalId);
        var current = await RequireAsync(scope, id, cancellationToken);
        if (current.Value.Status is ResourcePlanStatus.Applied or ResourcePlanStatus.Archived or ResourcePlanStatus.Cancelled)
            throw new ResourcePlanLifecycleException("resource_plan_not_refinable", $"A Resource Plan in status '{current.Value.Status}' cannot be refined.");
        var now = timeProvider.GetUtcNow();
        var next = current.Value with
        {
            Title = Required(request.Title, nameof(request.Title), 160),
            Goal = Required(request.Goal, nameof(request.Goal), 4000),
            Description = Optional(request.Description, 8000),
            Content = ValidateContent(request.Content),
            Status = ResourcePlanStatus.Draft,
            Revision = checked(current.Value.Revision + 1),
            UpdatedAt = now
        };
        var stored = await repository.UpdateAsync(next, RequiredETag(expectedETag), cancellationToken);
        await repository.AddActivityAsync(Activity(next, ResourcePlanActivityType.Refined, actorPrincipalId, null, now), cancellationToken);
        return stored;
    }

    public async Task<ResourcePlanSnapshot> ChangeStatusAsync(
        ResourcePlanScope scope,
        ResourcePlanId id,
        ChangeResourcePlanStatusRequest request,
        string expectedETag,
        Guid actorPrincipalId,
        CancellationToken cancellationToken)
    {
        ValidateActor(actorPrincipalId);
        var current = await RequireAsync(scope, id, cancellationToken);
        if (!Allowed(current.Value.Status, request.Status))
            throw new ResourcePlanLifecycleException("resource_plan_transition_invalid", $"Resource Plan status cannot change from '{current.Value.Status}' to '{request.Status}'.");
        var now = timeProvider.GetUtcNow();
        var next = current.Value with
        {
            Status = request.Status,
            Revision = checked(current.Value.Revision + 1),
            UpdatedAt = now
        };
        var stored = await repository.UpdateAsync(next, RequiredETag(expectedETag), cancellationToken);
        var type = request.Status switch
        {
            ResourcePlanStatus.Archived => ResourcePlanActivityType.Archived,
            ResourcePlanStatus.Cancelled => ResourcePlanActivityType.Cancelled,
            ResourcePlanStatus.Failed => ResourcePlanActivityType.Failed,
            _ => ResourcePlanActivityType.StatusChanged
        };
        await repository.AddActivityAsync(Activity(next, type, actorPrincipalId, Optional(request.Detail, 4000), now), cancellationToken);
        return stored;
    }

    public Task<IReadOnlyList<ResourcePlanActivity>> ListActivitiesAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken) =>
        repository.ListActivitiesAsync(scope, id, cancellationToken);

    public async Task<ResourcePlanSnapshot> MarkAppliedAsync(ResourcePlanScope scope, ResourcePlanId id, Guid actorPrincipalId, CancellationToken cancellationToken)
    {
        ValidateActor(actorPrincipalId);
        var current = await RequireAsync(scope, id, cancellationToken);
        if (current.Value.Status == ResourcePlanStatus.Applied) return current;
        var now = timeProvider.GetUtcNow();
        var next = current.Value with { Status = ResourcePlanStatus.Applied, UpdatedAt = now };
        var stored = await repository.UpdateAsync(next, current.ETag, cancellationToken);
        await repository.AddActivityAsync(Activity(next, ResourcePlanActivityType.Applied, actorPrincipalId, null, now), cancellationToken);
        return stored;
    }

    public async Task<ResourcePlanBindingDraftSnapshot?> GetBindingsAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken)
    {
        _ = await RequireAsync(scope, id, cancellationToken);
        return await repository.GetBindingsAsync(scope, id, cancellationToken);
    }

    public async Task<ResourcePlanBindingDraftSnapshot> SaveBindingsAsync(ResourcePlanScope scope, ResourcePlanId id,
        SaveResourcePlanBindingsRequest request, string? expectedETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await RequireAsync(scope, id, cancellationToken);
        if (plan.Value.Revision != request.PlanRevision)
            throw new ResourcePlanConcurrencyException("The Resource Plan changed. Reload it before selecting profiles.");
        if (plan.Value.Status is ResourcePlanStatus.Applied or ResourcePlanStatus.Archived or ResourcePlanStatus.Cancelled)
            throw new ResourcePlanLifecycleException("resource_plan_bindings_closed", "This Resource Plan no longer accepts profile selections.");
        var roles = plan.Value.Content.Document.Deserialize<FunctionalResourcePlanV1>(JsonOptions)?.Roles
            .Select(value => value.LogicalId).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (request.Bindings is null || request.Bindings.Count > roles.Count || request.Bindings.Any(value => !roles.Contains(value.LogicalId))
            || request.Bindings.Select(value => value.LogicalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Bindings.Count)
            throw new ArgumentException("Profile selections must refer to distinct roles in the current plan.", nameof(request));
        var draft = new ResourcePlanBindingDraft(id, scope, plan.Value.Revision, request.Bindings.ToArray(), timeProvider.GetUtcNow());
        return await repository.SaveBindingsAsync(draft, expectedETag, cancellationToken);
    }

    private async Task<ResourcePlanSnapshot> RequireAsync(ResourcePlanScope scope, ResourcePlanId id, CancellationToken cancellationToken) =>
        await GetAsync(scope, id, cancellationToken) ?? throw new ResourcePlanNotFoundException(id);

    private static ResourcePlanActivity Activity(ResourcePlan plan, ResourcePlanActivityType type, Guid actor, string? detail, DateTimeOffset now) =>
        new(Guid.NewGuid(), plan.Id, plan.Scope, plan.Revision, type, actor, detail, now);

    private static bool Allowed(ResourcePlanStatus from, ResourcePlanStatus to) => (from, to) switch
    {
        (ResourcePlanStatus.Draft, ResourcePlanStatus.Ready or ResourcePlanStatus.Cancelled) => true,
        (ResourcePlanStatus.Ready, ResourcePlanStatus.Draft or ResourcePlanStatus.Materialized or ResourcePlanStatus.Cancelled) => true,
        (ResourcePlanStatus.Materialized, ResourcePlanStatus.Draft or ResourcePlanStatus.Validated or ResourcePlanStatus.Failed or ResourcePlanStatus.Cancelled) => true,
        (ResourcePlanStatus.Validated, ResourcePlanStatus.Draft or ResourcePlanStatus.Applied or ResourcePlanStatus.Failed or ResourcePlanStatus.Cancelled) => true,
        (ResourcePlanStatus.Failed, ResourcePlanStatus.Draft or ResourcePlanStatus.Ready or ResourcePlanStatus.Cancelled) => true,
        (ResourcePlanStatus.Applied, ResourcePlanStatus.Archived) => true,
        _ => false
    };

    private static void ValidateScope(ResourcePlanScope scope)
    {
        if (scope.TenantId == Guid.Empty || scope.WorkspaceId.Value == Guid.Empty)
            throw new ArgumentException("Resource Plans require a Tenant and Workspace scope.", nameof(scope));
    }

    private static void ValidateActor(Guid actorPrincipalId)
    {
        if (actorPrincipalId == Guid.Empty) throw new ArgumentException("An actor Principal is required.", nameof(actorPrincipalId));
    }

    private ResourcePlanContent ValidateContent(ResourcePlanContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content.SchemaVersion) || content.SchemaVersion.Length > 64)
            throw new ArgumentException("Resource Plan content requires a schema version of at most 64 characters.", nameof(content));
        if (content.Document.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new ArgumentException("Resource Plan content must contain a JSON document.", nameof(content));
        var normalized = content with { SchemaVersion = content.SchemaVersion.Trim(), Document = content.Document.Clone() };
        var validation = contentValidator.Validate(normalized);
        if (!validation.IsValid) throw new ResourcePlanValidationException(validation.Issues);
        return normalized;
    }

    private static string Required(string value, string name, int maximum)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length is 0 || value.Length > maximum) throw new ArgumentException($"{name} must contain between 1 and {maximum} characters.", name);
        return value;
    }

    private static string? Optional(string? value, int maximum)
    {
        value = value?.Trim();
        if (value?.Length > maximum) throw new ArgumentException($"The value cannot exceed {maximum} characters.");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string RequiredETag(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ResourcePlanConcurrencyException("An If-Match ETag is required.")
        : value;
}

public sealed class ResourcePlanLifecycleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ResourcePlanValidationException(IReadOnlyList<PlanningValidationIssue> issues)
    : Exception("The functional Resource Plan document is invalid.")
{
    public IReadOnlyList<PlanningValidationIssue> Issues { get; } = issues;
}
