using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Work;

public static class WorkplaceApiVersions
{
    public const string V20260805 = "2026-08-05";
}

public static class WorkResourceTypes
{
    public const string Workspaces = "Agentstration.Work/workspaces";
    public const string Entries = "Agentstration.Work/entries";
}

public readonly record struct WorkplaceWorkspaceId(string Value, ResourceNamespace Namespace = default)
{
    public override string ToString() => Value;
}

public readonly record struct EntryId(string Value, ResourceNamespace Namespace = default)
{
    public override string ToString() => Value;
}

public readonly record struct InteractionId(Guid Value)
{
    public static InteractionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct WorkTaskId(Guid Value)
{
    public static WorkTaskId FromWorkItem(WorkItemId value) => new(value.Value);
    public WorkItemId ToWorkItemId() => new(Value);
    public override string ToString() => Value.ToString();
}

public readonly record struct PendingActionId(Guid Value) { public static PendingActionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString(); }
public readonly record struct WorkNotificationId(Guid Value) { public static WorkNotificationId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString(); }
public readonly record struct WorkTaskActivityId(Guid Value) { public static WorkTaskActivityId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString(); }
public readonly record struct WorkTaskResultId(Guid Value) { public static WorkTaskResultId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString(); }
public readonly record struct WorkTaskArtifactId(Guid Value) { public static WorkTaskArtifactId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString(); }

public enum WorkspaceEntryRole { Primary, Featured, Standard }
public enum EntryPresentationKind { Prompt, Form, Conversation, Action, FileDrop }
public enum EntryFieldType { Prompt, Text, Textarea, Number, Boolean, Choice, MultiChoice, Date, DateTime, File, Files, EntityPicker, ResourcePicker, Secret, Conversation }
public enum EntryFieldRole { Standard, PrimaryInput }
public enum EntryBindingKind { Agent, Flow }
public enum EntryVersionStrategy { Pinned }
public enum TaskCreationMode { Automatic, OnDemand, Never }
public enum InteractionStatus { Active, WaitingForUser, ConvertedToTask, Completed, Cancelled, Failed, Processing, Idle, Closed }
public enum WorkTaskStatus { Draft, Pending, Running, ActionRequired, Paused, Completed, Failed, Cancelled }
public enum PendingActionKind { InputRequired, ConfirmationRequired, ChoiceRequired, FileRequired, ApprovalRequired }
public enum PendingActionStatus { Pending, Completed, Cancelled, Expired }
public enum ConversationRole { User, Agentstration, System }
public enum WorkActorKind { User, Agentstration, System }
public enum WorkTaskActivityType { TaskCreated, TaskStarted, TaskPaused, TaskResumed, TaskCancelled, ActionRequired, ActionResolved, ResultProduced, ArtifactProduced, TaskCompleted, TaskFailed }
public enum WorkNotificationKind { ActionRequired, TaskCompleted, TaskFailed, Information }
public enum WorkTaskResultKind { Text, Structured, Table, Json, Status }

public sealed record WorkspaceEntryReference
{
    public required EntryId EntryResourceId { get; init; }
    public WorkspaceEntryRole Role { get; init; } = WorkspaceEntryRole.Standard;
    public int Order { get; init; }
}

public sealed record WorkplaceWorkspace
{
    public required WorkplaceWorkspaceId Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = WorkResourceTypes.Workspaces;
    public string ApiVersion { get; init; } = WorkplaceApiVersions.V20260805;
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<WorkspaceEntryReference> Entries { get; init; } = [];
    public int Version { get; init; } = 1;
    public DateTimeOffset PublishedAt { get; init; }
}

public sealed record WorkplaceWorkspaceDraft
{
    public required WorkplaceWorkspaceId Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<WorkspaceEntryReference> Entries { get; init; } = [];
    public long Revision { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record EntryFieldValidation(int? MinimumLength = null, int? MaximumLength = null, IReadOnlyList<string>? AllowedExtensions = null);
public sealed record EntryFieldOption(string Value, string Label);
public sealed record EntrySuggestion(string Label, string Value);

public sealed record EntryFieldDefinition
{
    public required string Name { get; init; }
    public required EntryFieldType Type { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }
    public string? Placeholder { get; init; }
    public bool Required { get; init; }
    public JsonElement? DefaultValue { get; init; }
    public IReadOnlyList<EntryFieldOption> Options { get; init; } = [];
    public int Order { get; init; }
    public EntryFieldValidation? Validation { get; init; }
    public EntryFieldRole Role { get; init; }
}

public sealed record EntryPresentation
{
    public EntryPresentationKind Kind { get; init; } = EntryPresentationKind.Prompt;
    public string? Placeholder { get; init; }
    public string? Icon { get; init; }
    public bool AllowAttachments { get; init; }
    public bool AllowVoiceInput { get; init; }
    public IReadOnlyList<EntrySuggestion> Suggestions { get; init; } = [];
    public IReadOnlyList<EntryFieldDefinition> Fields { get; init; } = [];
}

public sealed record EntryBinding(EntryBindingKind Kind, string ResourceId, ResourceNamespace? Namespace = null);
public sealed record EntryResolvedTarget(string FlowResourceId, string Version, EntryVersionStrategy VersionStrategy = EntryVersionStrategy.Pinned)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record EntryConversationBehavior(bool Enabled = true, EntryResolvedTarget? ContinuationTarget = null);
public sealed record EntryBehavior(TaskCreationMode TaskCreationMode = TaskCreationMode.Automatic, bool AllowConversation = true, bool StreamResponse = true, EntryConversationBehavior? Conversation = null);

public sealed record EntryDraft
{
    public required EntryId Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = WorkResourceTypes.Entries;
    public string ApiVersion { get; init; } = WorkplaceApiVersions.V20260805;
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required EntryPresentation Presentation { get; init; }
    public required EntryBinding Binding { get; init; }
    public EntryBinding? PublishedBinding { get; init; }
    public EntryBehavior Behavior { get; init; } = new();
    public long Revision { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record EntryResource
{
    public required EntryId Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = WorkResourceTypes.Entries;
    public string ApiVersion { get; init; } = WorkplaceApiVersions.V20260805;
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required EntryPresentation Presentation { get; init; }
    public required EntryResolvedTarget ResolvedTarget { get; init; }
    public EntryBehavior Behavior { get; init; } = new();
    public int Version { get; init; } = 1;
    public DateTimeOffset PublishedAt { get; init; }
}

public sealed record EntryDependency(string ResourceId, string ResourceType, string Relationship);

public sealed record ConversationMessage(
    Guid Id, WorkplaceWorkspaceId WorkspaceId, InteractionId InteractionId, WorkTaskId? WorkTaskId,
    ConversationRole Role, string Content, DateTimeOffset CreatedAt, string? AgentResourceId = null,
    IReadOnlyList<WorkAttachment>? Attachments = null, PendingActionId? PendingActionId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record PendingActionResponse(IReadOnlyDictionary<string, JsonElement> Values, DateTimeOffset SubmittedAt);

public sealed record PendingAction
{
    public required PendingActionId Id { get; init; }
    public required WorkplaceWorkspaceId WorkspaceId { get; init; }
    public required InteractionId InteractionId { get; init; }
    public WorkTaskId? WorkTaskId { get; init; }
    public string? FlowRunId { get; init; }
    public required PendingActionKind Kind { get; init; }
    public PendingActionStatus Status { get; init; } = PendingActionStatus.Pending;
    public required string Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<EntryFieldDefinition> Fields { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public PendingActionResponse? Response { get; init; }
    public required string ResumeTokenHash { get; init; }
    public int ResumeStep { get; init; }
    public long Version { get; init; } = 1;
}

public sealed record WorkTaskActivity(
    WorkTaskActivityId Id, WorkplaceWorkspaceId WorkspaceId, WorkTaskId WorkTaskId,
    WorkTaskActivityType Type, string Title, string? Description, DateTimeOffset CreatedAt,
    WorkActorKind ActorKind, string? FlowRunId = null, IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record WorkTaskResult(
    WorkTaskResultId Id, WorkplaceWorkspaceId WorkspaceId, WorkTaskId WorkTaskId, string? FlowRunId,
    WorkTaskResultKind Kind, string Title, JsonElement Content, DateTimeOffset CreatedAt, int Sequence = 1);

public sealed record ArtifactReference(string StorageKey, string ContentType, long Length);
public sealed record ArtifactContent(string Name, string ContentType, Stream Content);
public sealed record WorkTaskArtifact(
    WorkTaskArtifactId Id, WorkplaceWorkspaceId WorkspaceId, WorkTaskId WorkTaskId, string? FlowRunId,
    string Name, string ContentType, long Length, string StorageKey, DateTimeOffset CreatedAt, int Sequence = 1);

public sealed record WorkNotification
{
    public required WorkNotificationId Id { get; init; }
    public required WorkplaceWorkspaceId WorkspaceId { get; init; }
    public required WorkNotificationKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReadAt { get; init; }
    public WorkTaskId? WorkTaskId { get; init; }
    public InteractionId? InteractionId { get; init; }
    public PendingActionId? PendingActionId { get; init; }
    public string? ActionUrl { get; init; }
    public long Version { get; init; } = 1;
}

public sealed record WorkplaceInteraction
{
    public required InteractionId Id { get; init; }
    public required WorkplaceWorkspaceId WorkspaceId { get; init; }
    public required EntryId EntryId { get; init; }
    public InteractionStatus Status { get; init; } = InteractionStatus.Active;
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastActivityAt { get; init; }
    public IReadOnlyDictionary<string, JsonElement> InputValues { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<WorkAttachment> Attachments { get; init; } = [];
    public IReadOnlyList<ConversationMessage> Messages { get; init; } = [];
    public PendingActionId? PendingActionId { get; init; }
    public WorkTaskId? TaskId { get; init; }
    public string? LastFlowRunId { get; init; }
    public Guid? LastTriggerMessageId { get; init; }
    public WorkplaceAction? ImmediateResult { get; init; }
    public long Version { get; init; } = 1;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RespondAction), "respond")]
[JsonDerivedType(typeof(RequestInputAction), "requestInput")]
[JsonDerivedType(typeof(RequestConfirmationAction), "requestConfirmation")]
[JsonDerivedType(typeof(RequestChoiceAction), "requestChoice")]
[JsonDerivedType(typeof(CreateTaskAction), "createTask")]
[JsonDerivedType(typeof(ShowResultAction), "showResult")]
[JsonDerivedType(typeof(ShowErrorAction), "showError")]
public abstract record WorkplaceAction;
public sealed record RespondAction(string Content) : WorkplaceAction;
public sealed record RequestInputAction(string Title, string? Description, IReadOnlyList<EntryFieldDefinition> Fields, PendingActionId PendingActionId, string ResumeToken) : WorkplaceAction;
public sealed record RequestConfirmationAction(string Title, string? Description, PendingActionId PendingActionId, string ResumeToken) : WorkplaceAction;
public sealed record RequestChoiceAction(string Title, string? Description, IReadOnlyList<EntryFieldOption> Options, PendingActionId PendingActionId, string ResumeToken, string FieldName = "detailLevel") : WorkplaceAction;
public sealed record CreateTaskAction(WorkTaskId TaskId, string Title, string? Description, string Location) : WorkplaceAction;
public sealed record ShowResultAction(string Title, string? Text, JsonElement? Structured = null) : WorkplaceAction;
public sealed record ShowErrorAction(string Title, string? Description) : WorkplaceAction;

public sealed record WorkTask(
    WorkTaskId Id,
    WorkplaceWorkspaceId WorkspaceId,
    EntryId EntryId,
    InteractionId InteractionId,
    string Title,
    string? Description,
    WorkTaskStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? FlowRunId,
    IReadOnlyList<WorkMessage> Conversation,
    IReadOnlyList<WorkInteraction> Activities,
    IReadOnlyList<WorkArtifact> Artifacts,
    WorkResult? Result,
    WorkError? Error,
    long Version);

public static class WorkplaceValidation
{
    public static void Validate(WorkplaceWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateName(workspace.Id.Value, "workspace_id_invalid");
        if (!string.Equals(workspace.Id.Value, workspace.Name, StringComparison.Ordinal))
            throw new WorkValidationException("workspace_identity_mismatch", "Workspace id and name must match.");
        if (string.IsNullOrWhiteSpace(workspace.DisplayName)) throw new WorkValidationException("workspace_display_name_required", "A Workspace display name is required.");
        if (workspace.Entries.Count(reference => reference.Role == WorkspaceEntryRole.Primary) > 1)
            throw new WorkValidationException("workspace_primary_entry_conflict", "A Workspace can expose at most one Primary Entry.");
        if (workspace.Entries.Select(value => value.EntryResourceId).Distinct().Count() != workspace.Entries.Count)
            throw new WorkValidationException("workspace_entry_duplicate", "A Workspace cannot reference the same Entry more than once.");
    }

    public static void Validate(WorkplaceWorkspaceDraft workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateName(workspace.Id.Value, "workspace_id_invalid");
        if (!string.Equals(workspace.Id.Value, workspace.Name, StringComparison.Ordinal))
            throw new WorkValidationException("workspace_identity_mismatch", "Workspace id and name must match.");
        if (string.IsNullOrWhiteSpace(workspace.DisplayName)) throw new WorkValidationException("workspace_display_name_required", "A Workspace display name is required.");
        if (workspace.Entries.Count(reference => reference.Role == WorkspaceEntryRole.Primary) > 1)
            throw new WorkValidationException("workspace_primary_entry_conflict", "A Workspace can expose at most one Primary Entry.");
        if (workspace.Entries.Select(value => value.EntryResourceId).Distinct().Count() != workspace.Entries.Count)
            throw new WorkValidationException("workspace_entry_duplicate", "A Workspace cannot reference the same Entry more than once.");
    }

    public static void Validate(EntryResource entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateName(entry.Id.Value, "entry_id_invalid");
        if (!string.Equals(entry.Id.Value, entry.Name, StringComparison.Ordinal))
            throw new WorkValidationException("entry_identity_mismatch", "Entry id and name must match.");
        if (string.IsNullOrWhiteSpace(entry.DisplayName)) throw new WorkValidationException("entry_display_name_required", "An Entry display name is required.");
        if (string.IsNullOrWhiteSpace(entry.ResolvedTarget.FlowResourceId)) throw new WorkValidationException("entry_target_required", "A published Entry requires a resolved Flow target.");
        _ = FlowReferenceFrom(entry.ResolvedTarget);
        ValidatePresentation(entry.Presentation);
    }

    public static void Validate(EntryDraft entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateName(entry.Id.Value, "entry_id_invalid");
        if (!string.Equals(entry.Id.Value, entry.Name, StringComparison.Ordinal))
            throw new WorkValidationException("entry_identity_mismatch", "Entry id and name must match.");
        if (string.IsNullOrWhiteSpace(entry.DisplayName)) throw new WorkValidationException("entry_display_name_required", "An Entry display name is required.");
        ValidateBinding(entry.Binding);
        ValidatePresentation(entry.Presentation);
    }

    private static void ValidatePresentation(EntryPresentation presentation)
    {
        if (presentation.Kind is not EntryPresentationKind.Prompt and not EntryPresentationKind.Form)
            throw new WorkValidationException("entry_kind_not_supported", "The MVP supports Prompt and Form Entries.");
        if (presentation.Kind == EntryPresentationKind.Form && presentation.Fields.Count == 0)
            throw new WorkValidationException("entry_fields_required", "A Form Entry requires at least one field.");
        if (presentation.Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != presentation.Fields.Count)
            throw new WorkValidationException("entry_field_duplicate", "Entry field names must be unique.");
        foreach (var field in presentation.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name)) throw new WorkValidationException("entry_field_name_required", "Every Entry field requires a name.");
            if (!Enum.IsDefined(field.Type)) throw new WorkValidationException("entry_field_type_invalid", $"Entry field '{field.Name}' has an unsupported type.");
            if (field.Validation is { MinimumLength: int minimum, MaximumLength: int maximum } && minimum > maximum)
                throw new WorkValidationException("entry_field_validation_invalid", $"Entry field '{field.Name}' has an invalid length range.");
            if (field.Type is EntryFieldType.Choice or EntryFieldType.MultiChoice)
            {
                if (field.Options.Count == 0) throw new WorkValidationException("entry_field_options_required", $"Entry field '{field.Name}' requires at least one option.");
                if (field.Options.Any(option => string.IsNullOrWhiteSpace(option.Value) || string.IsNullOrWhiteSpace(option.Label))
                    || field.Options.Select(option => option.Value).Distinct(StringComparer.Ordinal).Count() != field.Options.Count)
                    throw new WorkValidationException("entry_field_options_invalid", $"Entry field '{field.Name}' options require unique non-empty values and labels.");
            }
            else if (field.Options.Count > 0)
            {
                throw new WorkValidationException("entry_field_options_not_supported", $"Entry field '{field.Name}' only supports options when its type is Choice or MultiChoice.");
            }
        }
        if (presentation.Suggestions.Any(value => string.IsNullOrWhiteSpace(value.Label) || string.IsNullOrWhiteSpace(value.Value))
            || presentation.Suggestions.Select(value => value.Label).Distinct(StringComparer.Ordinal).Count() != presentation.Suggestions.Count)
            throw new WorkValidationException("entry_suggestions_invalid", "Entry suggestions require unique non-empty labels and values.");
        var primary = presentation.Fields.Count(field => field.Role == EntryFieldRole.PrimaryInput);
        if (primary != 1) throw new WorkValidationException("entry_primary_input_required", "An Entry requires exactly one primary input field.");
    }

    public static void ValidateBinding(EntryBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(binding.ResourceId) || binding.ResourceId.Contains('/', StringComparison.Ordinal))
            throw new WorkValidationException("entry_binding_invalid", $"The {binding.Kind} binding name is invalid.");
    }

    public static void ValidateSubmission(EntryResource entry, IReadOnlyDictionary<string, JsonElement> values)
        => ValidateFields(entry.Presentation.Fields, values);

    public static void ValidateFields(IReadOnlyList<EntryFieldDefinition> fields, IReadOnlyDictionary<string, JsonElement> values)
    {
        foreach (var field in fields)
        {
            values.TryGetValue(field.Name, out var value);
            var missing = value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString());
            if (field.Required && missing) throw new WorkValidationException("entry_field_required", $"Field '{field.Name}' is required.");
            if (missing) continue;
            var validKind = field.Type switch
            {
                EntryFieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                EntryFieldType.Number => value.ValueKind == JsonValueKind.Number,
                EntryFieldType.MultiChoice or EntryFieldType.Files => value.ValueKind == JsonValueKind.Array,
                _ => value.ValueKind == JsonValueKind.String
            };
            if (!validKind) throw new WorkValidationException("entry_field_type_invalid", $"Field '{field.Name}' has an invalid value type.");
            if (field.Type == EntryFieldType.Choice && field.Options.Count > 0
                && !field.Options.Any(option => string.Equals(option.Value, value.GetString(), StringComparison.Ordinal)))
                throw new WorkValidationException("entry_field_choice_invalid", $"Field '{field.Name}' has an unsupported value.");
            if (value.ValueKind == JsonValueKind.String)
            {
                var length = value.GetString()!.Length;
                if (field.Validation?.MinimumLength is int minimum && length < minimum)
                    throw new WorkValidationException("entry_field_too_short", $"Field '{field.Name}' is shorter than {minimum} characters.");
                if (field.Validation?.MaximumLength is int maximum && length > maximum)
                    throw new WorkValidationException("entry_field_too_long", $"Field '{field.Name}' exceeds {maximum} characters.");
            }
        }
    }

    public static FlowReference FlowReferenceFrom(EntryResolvedTarget target)
    {
        var flowName = target.FlowResourceId;
        if (string.IsNullOrWhiteSpace(flowName) || flowName.Contains('/', StringComparison.Ordinal))
            throw new WorkValidationException("entry_target_not_supported", "The Entry target must reference a Flow name.");
        if (string.IsNullOrWhiteSpace(target.Version)) throw new WorkValidationException("entry_target_version_required", "A published Entry target version is required.");
        return new FlowReference(new FlowId(flowName, target.Namespace), target.Version, UseActiveVersion: false, target.Namespace);
    }

    private static void ValidateName(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !char.IsLetterOrDigit(value[0])
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new WorkValidationException(code, "Identifiers must contain 1 to 128 letters, digits, '-' or '_' and start with a letter or digit.");
    }
}
