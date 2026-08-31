using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Work;

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
