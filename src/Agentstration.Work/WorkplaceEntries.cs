using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Work;

public sealed record EntryFieldValidation(int? MinimumLength = null, int? MaximumLength = null, IReadOnlyList<string>? AllowedExtensions = null);
public sealed record EntryFieldOption(string Value, string Label);
public sealed record EntrySuggestion(string Label, string Value);
public sealed record EntryParticipantsPresentation(EntryParticipantVisibility Visibility = EntryParticipantVisibility.Hidden);
public sealed record EntryProgressPresentation(EntryProgressVisibility Visibility = EntryProgressVisibility.Compact);
public sealed record EntryTaskPresentation(EntryTaskDisplay Display = EntryTaskDisplay.Auto);
public sealed record EntryResultsPresentation(EntryResultDisplay Display = EntryResultDisplay.Auto);

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
    public EntryParticipantsPresentation Participants { get; init; } = new();
    public EntryProgressPresentation Progress { get; init; } = new();
    public EntryTaskPresentation Task { get; init; } = new();
    public EntryResultsPresentation Results { get; init; } = new();
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
    public required WorkspaceId WorkspaceId { get; init; }
    public required EntryId Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = WorkResourceTypes.Entries;
    public string ApiVersion { get; init; } = WorkplaceApiVersions.CoreV1;
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
    public required WorkspaceId WorkspaceId { get; init; }
    public required EntryId Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = WorkResourceTypes.Entries;
    public string ApiVersion { get; init; } = WorkplaceApiVersions.CoreV1;
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required EntryPresentation Presentation { get; init; }
    public required EntryResolvedTarget ResolvedTarget { get; init; }
    public EntryBehavior Behavior { get; init; } = new();
    public int Version { get; init; } = 1;
    public DateTimeOffset PublishedAt { get; init; }
}

public sealed record EntryDependency(string ResourceId, string ResourceType, string Relationship);
