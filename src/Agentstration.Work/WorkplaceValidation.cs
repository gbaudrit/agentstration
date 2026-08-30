using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Work;

public static class WorkplaceValidation
{
    public static void Validate(WorkplaceDashboard dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ValidateDashboard(dashboard.Id, dashboard.WorkspaceId, dashboard.Name, dashboard.DisplayName, dashboard.Entries);
    }

    public static void Validate(WorkplaceDashboardDraft dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ValidateDashboard(dashboard.Id, dashboard.WorkspaceId, dashboard.Name, dashboard.DisplayName, dashboard.Entries);
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
        ArgumentNullException.ThrowIfNull(presentation.Participants);
        ArgumentNullException.ThrowIfNull(presentation.Progress);
        ArgumentNullException.ThrowIfNull(presentation.Task);
        ArgumentNullException.ThrowIfNull(presentation.Results);
        if (!Enum.IsDefined(presentation.Participants.Visibility)
            || !Enum.IsDefined(presentation.Progress.Visibility)
            || !Enum.IsDefined(presentation.Task.Display)
            || !Enum.IsDefined(presentation.Results.Display))
            throw new WorkValidationException("entry_execution_presentation_invalid", "The Entry execution presentation contains an unsupported value.");
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

    private static void ValidateDashboard(
        DashboardId id,
        WorkspaceId workspaceId,
        string name,
        string displayName,
        IReadOnlyList<DashboardEntryReference> entries)
    {
        if (workspaceId.Value == Guid.Empty)
            throw new WorkValidationException("workspace_id_invalid", "A canonical Workspace id is required.");
        ValidateName(id.Value, "dashboard_id_invalid");
        if (!string.Equals(id.Value, name, StringComparison.Ordinal))
            throw new WorkValidationException("dashboard_identity_mismatch", "Dashboard id and name must match.");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new WorkValidationException("dashboard_display_name_required", "A Dashboard display name is required.");
        if (entries.Count(reference => reference.Role == DashboardItemRole.Primary) > 1)
            throw new WorkValidationException("dashboard_primary_entry_conflict", "A Dashboard can expose at most one Primary Entry.");
        if (entries.Select(value => value.EntryResourceId).Distinct().Count() != entries.Count)
            throw new WorkValidationException("dashboard_entry_duplicate", "A Dashboard cannot reference the same Entry more than once.");
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
