using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Agentstration.Work;

namespace Agentstration.Web.Components.Pages;

public partial class EntryEditor
{
    private static readonly JsonSerializerOptions EntryIndentedJson = new() { WriteIndented = true };
    private bool rawPublish;
    private RawEntryResource RawResource() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = EntryResourceKinds.Entry,
        Metadata = new() { Name = model.Name.Trim() },
        Definition = new() { DisplayName = model.DisplayName.Trim(), Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim(), Presentation = model.Presentation(), Exposure = model.Exposure(), Binding = CurrentBinding(), Behavior = model.Behavior(), Publish = rawPublish }
    };
    private void PrepareRawYaml()
    {
        if (HasRawDraft) return;
        rawError = null;
        try { rawPublish = published is not null; rawYaml = rawBaseline = ResourceManifestSerializer.ToYaml(RawResource()); }
        catch (Exception exception) { rawError = exception.Message; }
    }
    private void RawChanged(string value) { if (CanWrite && !busy) rawYaml = value; }
    private void DiscardYaml() { if (busy) return; rawYaml = rawBaseline; rawError = null; PrepareRawYaml(); }
    private void ToggleYaml() { if (sectionState.Is("yaml")) sectionState.Select("definition/identity"); else { PrepareRawYaml(); sectionState.Select("yaml"); } }
    private Task ApplyRawAsync() { if (ApplyRawYaml()) rawBaseline = rawYaml; return Task.CompletedTask; }
    private bool ApplyRawYaml()
    {
        if (busy || !CanWrite) return false;
        rawError = null;
        try
        {
            var value = ResourceManifestSerializer.FromYaml<RawEntryResource>(rawYaml);
            if (!string.Equals(value.ApiVersion, ResourceApiVersions.CoreV1, StringComparison.Ordinal)) throw new ArgumentException(T("ApiVersionRequired", ResourceApiVersions.CoreV1));
            if (!string.Equals(value.Kind, EntryResourceKinds.Entry, StringComparison.Ordinal)) throw new ArgumentException(T("KindRequired", EntryResourceKinds.Entry));
            if (!IsNew && !string.Equals(value.Metadata.Name, Name, StringComparison.Ordinal)) throw new ArgumentException(T("EntryNameImmutable"));
            var draft = new EntryDraft
            {
                WorkspaceId = model.WorkspaceId,
                Id = new(value.Metadata.Name, EntryResourceNamespace),
                Name = value.Metadata.Name,
                DisplayName = value.Definition.DisplayName ?? value.Metadata.Name,
                Description = value.Definition.Description,
                Presentation = value.Definition.Presentation,
                Exposure = value.Definition.Exposure,
                Binding = value.Definition.Binding,
                Behavior = value.Definition.Behavior,
                PublishedBinding = publishedBinding,
                Revision = draftRevision
            };
            // The raw source is an explicit transfer. Keep its exact text on parser/command rejection.
            model = EntryEditorModel.From(draft, BindingKey(draft.Binding, EntryResourceNamespace)); declaredBinding = draft.Binding; bindingEdited = invalidInput = false; rawPublish = value.Definition.Publish;
            ReplaceContext(); message = T("YamlApplied"); hasError = false; return true;
        }
        catch (Exception exception) { rawError = exception.Message; return false; }
    }
    private async Task SaveRawAsync() { if (ApplyRawYaml()) await ExecuteCommandAsync(rawPublish ? EntryCommand.Publish : EntryCommand.Save, fromYaml: true); }
    private sealed record ConfigurationEntry(string Label, string Value);
    private IReadOnlyList<ConfigurationEntry> SavedConfiguration => saved is null ? [] : Configuration(saved.Value);
    private IReadOnlyList<ConfigurationEntry> DraftConfiguration => Configuration(Draft());
    private IReadOnlyList<ConfigurationEntry> Configuration(EntryDraft value)
    {
        var values = new List<ConfigurationEntry>
        {
            new(T("Presentation"), EnumLabel("Presentation", value.Presentation.Kind)),
            new(T("Binding"), BindingText(value.Binding, value.Id.Namespace)), new(T("EntryExposure"), Json(value.Exposure)),
            new(T("WorkplaceAppearance"), Json(new { value.Presentation.Placeholder, value.Presentation.Icon, value.Presentation.AllowAttachments, value.Presentation.AllowVoiceInput })),
            new(T("InteractionPresentation"), Json(new { value.Presentation.Participants, value.Presentation.Progress, value.Presentation.Task, value.Presentation.Results })),
            new(T("InputSchema"), Json(value.Presentation.Fields)), new(T("ConversationAndExecution"), Json(value.Behavior)), new(T("PromptStarters"), Json(value.Presentation.Suggestions))
        };
        if (published is not null) { values.Add(new(T("ResolvedTarget"), Json(published.ResolvedTarget))); values.Add(new(T("PublishedAt", FormatDate(published.PublishedAt)), published.Version.ToString(System.Globalization.CultureInfo.CurrentCulture))); }
        return values;
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EntryIndentedJson);
    private sealed record RawEntryMetadata { public required string Name { get; init; } }
    private sealed record RawEntryDefinition { public string? DisplayName { get; init; } public string? Description { get; init; } public required EntryPresentation Presentation { get; init; } public EntryExposure Exposure { get; init; } = new(); public required EntryBinding Binding { get; init; } public EntryBehavior Behavior { get; init; } = new(); public bool Publish { get; init; } }
    private sealed record RawEntryResource { public required string ApiVersion { get; init; } public required string Kind { get; init; } public required RawEntryMetadata Metadata { get; init; } public required RawEntryDefinition Definition { get; init; } }
}
