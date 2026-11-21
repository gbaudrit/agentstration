using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Web.Console;

public sealed class EntryEditorModel
{
    [Required, StringLength(128)] public string Name { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Namespace { get; set; } = ResourceNamespace.Default.Value;
    public WorkspaceId WorkspaceId { get; set; }
    public string Placeholder { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public EntryPresentationKind PresentationKind { get; set; } = EntryPresentationKind.Prompt;
    public EntryParticipantVisibility ParticipantsVisibility { get; set; } = EntryParticipantVisibility.Hidden;
    public EntryProgressVisibility ProgressVisibility { get; set; } = EntryProgressVisibility.Compact;
    public EntryTaskDisplay TaskDisplay { get; set; } = EntryTaskDisplay.Auto;
    public EntryResultDisplay ResultDisplay { get; set; } = EntryResultDisplay.Auto;
    public EntryBindingKind BindingKind { get; set; } = EntryBindingKind.Agent;
    public string ResourceId { get; set; } = string.Empty;
    public TaskCreationMode TaskMode { get; set; } = TaskCreationMode.Automatic;
    public bool AllowConversation { get; set; } = true;
    public bool StreamResponse { get; set; } = true;
    public bool AllowAttachments { get; set; }
    public bool AllowVoiceInput { get; set; }
    public bool ExposeWorkplace { get; set; } = true;
    public bool ExposeConsole { get; set; }
    public bool ExposeOwningSpace { get; set; } = true;
    public bool ExposeTenantHome { get; set; }
    public bool ConsoleFallback { get; set; }
    public List<EntryEditorField> Fields { get; } = [];
    public List<EntryEditorSuggestion> Suggestions { get; } = [];
    [JsonIgnore] public EntryDraft? Original { get; set; }

    public EntryPresentation Presentation() => new()
    {
        Kind = PresentationKind,
        Placeholder = NullIfWhiteSpace(Placeholder),
        Icon = NullIfWhiteSpace(Icon),
        AllowAttachments = AllowAttachments,
        AllowVoiceInput = AllowVoiceInput,
        Participants = new(ParticipantsVisibility),
        Progress = new(ProgressVisibility),
        Task = new(TaskDisplay),
        Results = new(ResultDisplay),
        Fields = Fields.Select(value => new EntryFieldDefinition
        {
            Name = value.Name.Trim(),
            Label = NullIfWhiteSpace(value.Label),
            Description = NullIfWhiteSpace(value.Description),
            Placeholder = NullIfWhiteSpace(value.Placeholder),
            Type = value.Type,
            Required = value.Required,
            Role = value.Primary ? EntryFieldRole.PrimaryInput : EntryFieldRole.Standard,
            Order = value.Order,
            Options = value.Original is { } old && value.Options == OptionsText(old.Options) ? old.Options : ParseOptions(value.Options),
            DefaultValue = value.DefaultValue,
            Validation = value.Validation
        }).ToArray(),
        Suggestions = Suggestions.Select(value => new EntrySuggestion(value.Label.Trim(), value.Value.Trim())).ToArray()
    };
    public EntryBehavior Behavior()
    {
        var original = Original?.Behavior;
        return new(TaskMode, AllowConversation, StreamResponse, original is not null && original.AllowConversation == AllowConversation
            ? original.Conversation : (original?.Conversation ?? new()) with { Enabled = AllowConversation });
    }
    public EntryExposure Exposure() => new()
    {
        Version = Original?.Exposure.Version ?? EntryExposure.CurrentVersion,
        Surfaces = Enum.GetValues<EntryExposureSurface>().Where(value => value switch { EntryExposureSurface.Workplace => ExposeWorkplace, EntryExposureSurface.Console => ExposeConsole, _ => false }).ToArray(),
        WorkplacePlacements = ExposeWorkplace ? Enum.GetValues<EntryWorkplacePlacement>().Where(value => value switch { EntryWorkplacePlacement.OwningSpace => ExposeOwningSpace, EntryWorkplacePlacement.TenantHome => ExposeTenantHome, _ => false }).ToArray() : [],
        Console = new(ExposeConsole && ConsoleFallback ? EntryConsoleRole.Fallback : EntryConsoleRole.Standard)
    };
    public EntryDraft Draft(EntryBinding binding, long revision, EntryBinding? publishedBinding) => new()
    {
        WorkspaceId = WorkspaceId,
        Id = new(Name.Trim(), ResourceNamespace.Parse(Namespace)),
        Name = Name.Trim(),
        DisplayName = DisplayName.Trim(),
        Description = NullIfWhiteSpace(Description),
        Presentation = Presentation(),
        Exposure = Exposure(),
        Binding = binding,
        PublishedBinding = publishedBinding,
        Behavior = Behavior(),
        Revision = revision,
        UpdatedAt = Original?.UpdatedAt ?? default
    };
    public static EntryEditorModel From(EntryDraft draft, string bindingKey)
    {
        var value = new EntryEditorModel
        {
            Original = draft,
            Name = draft.Name,
            Namespace = draft.Id.Namespace.Value,
            WorkspaceId = draft.WorkspaceId,
            DisplayName = draft.DisplayName,
            Description = draft.Description ?? "",
            Placeholder = draft.Presentation.Placeholder ?? "",
            Icon = draft.Presentation.Icon ?? "",
            PresentationKind = draft.Presentation.Kind,
            ParticipantsVisibility = draft.Presentation.Participants.Visibility,
            ProgressVisibility = draft.Presentation.Progress.Visibility,
            TaskDisplay = draft.Presentation.Task.Display,
            ResultDisplay = draft.Presentation.Results.Display,
            BindingKind = draft.Binding.Kind,
            ResourceId = bindingKey,
            TaskMode = draft.Behavior.TaskCreationMode,
            AllowConversation = draft.Behavior.AllowConversation,
            StreamResponse = draft.Behavior.StreamResponse,
            AllowAttachments = draft.Presentation.AllowAttachments,
            AllowVoiceInput = draft.Presentation.AllowVoiceInput,
            ExposeWorkplace = draft.Exposure.Surfaces.Contains(EntryExposureSurface.Workplace),
            ExposeConsole = draft.Exposure.Surfaces.Contains(EntryExposureSurface.Console),
            ExposeOwningSpace = draft.Exposure.WorkplacePlacements.Contains(EntryWorkplacePlacement.OwningSpace),
            ExposeTenantHome = draft.Exposure.WorkplacePlacements.Contains(EntryWorkplacePlacement.TenantHome),
            ConsoleFallback = draft.Exposure.Console.Role == EntryConsoleRole.Fallback
        };
        value.Fields.AddRange(draft.Presentation.Fields.Select(field => new EntryEditorField
        {
            Original = field,
            Name = field.Name,
            Label = field.Label ?? "",
            Description = field.Description ?? "",
            Placeholder = field.Placeholder ?? "",
            Type = field.Type,
            Required = field.Required,
            Primary = field.Role == EntryFieldRole.PrimaryInput,
            Options = OptionsText(field.Options),
            DefaultValue = field.DefaultValue,
            Validation = field.Validation,
            Order = field.Order
        }));
        value.Suggestions.AddRange(draft.Presentation.Suggestions.Select(suggestion => new EntryEditorSuggestion { Label = suggestion.Label, Value = suggestion.Value }));
        return value;
    }
    public static string? InvalidField(WorkValidationException exception) => exception.Code switch
    {
        "entry_id_invalid" or "entry_identity_mismatch" => nameof(Name),
        "entry_display_name_required" => nameof(DisplayName),
        "entry_binding_invalid" => nameof(ResourceId),
        "entry_exposure_version_unsupported" or "entry_exposure_surfaces_invalid" or "entry_console_role_invalid" or "entry_console_role_not_allowed" => nameof(ExposeConsole),
        "entry_workplace_placements_invalid" or "entry_workplace_placement_required" or "entry_workplace_placements_not_allowed" => nameof(ExposeOwningSpace),
        "entry_kind_not_supported" => nameof(PresentationKind),
        "entry_execution_presentation_invalid" => nameof(ParticipantsVisibility),
        "entry_suggestions_invalid" => nameof(Suggestions),
        _ => nameof(Fields)
    };
    private static string OptionsText(IReadOnlyList<EntryFieldOption> options) => string.Join(", ", options.Select(option => $"{option.Value}:{option.Label}"));
    private static IReadOnlyList<EntryFieldOption> ParseOptions(string options) => options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => { var parts = value.Split(':', 2, StringSplitOptions.TrimEntries); return new EntryFieldOption(parts[0], parts.Length == 2 ? parts[1] : parts[0]); }).ToArray();
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class EntryEditorField
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Placeholder { get; set; } = "";
    public string Options { get; set; } = "";
    public EntryFieldType Type { get; set; }
    public bool Required { get; set; }
    public bool Primary { get; set; }
    public int Order { get; set; }
    public JsonElement? DefaultValue { get; set; }
    public EntryFieldValidation? Validation { get; set; }
    [JsonIgnore] public EntryFieldDefinition? Original { get; set; }
}
public sealed class EntryEditorSuggestion { public string Label { get; set; } = ""; public string Value { get; set; } = ""; }
