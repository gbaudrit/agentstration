using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Work;

public static class WorkplaceApiVersions
{
    public const string CoreV1 = "agentstration.io/v1";
}

public static class WorkResourceTypes
{
    public const string Dashboards = "Agentstration.Work/dashboards";
    public const string Entries = "Agentstration.Work/entries";
}

public readonly record struct EntryId(string Value, ResourceNamespace Namespace = default)
{
    public override string ToString() => Value;
}

public readonly record struct DashboardId(string Value)
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

public enum DashboardItemRole { Primary, Featured, Standard }
public enum EntryPresentationKind { Prompt, Form, Conversation, Action, FileDrop }
public enum EntryParticipantVisibility { Hidden, Visible }
public enum EntryProgressVisibility { Hidden, Compact, Detailed }
public enum EntryTaskDisplay { Auto, Hidden, Visible }
public enum EntryResultDisplay { Auto, Hidden, Visible }
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
public enum WorkTaskActivityType { TaskCreated, TaskStarted, ProgressStarted, ProgressCompleted, TaskPaused, TaskResumed, TaskCancelled, ActionRequired, ActionResolved, ResultProduced, ArtifactProduced, TaskCompleted, TaskFailed }
public enum WorkNotificationKind { ActionRequired, TaskCompleted, TaskFailed, Information }
public enum WorkTaskResultKind { Text, Structured, Table, Json, Status }
