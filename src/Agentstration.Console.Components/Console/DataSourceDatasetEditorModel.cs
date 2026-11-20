using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Components.Pages;

namespace Agentstration.Web.Console;

public sealed class DataSourceDatasetEditorModel
{
    [Required] public string Name { get; set; } = "";
    [Required, StringLength(200)] public string DisplayName { get; set; } = "";
    public string Namespace { get; set; } = "default";
    [StringLength(2000)] public string? Description { get; set; } = "";
    public string Tags { get; set; } = "";
    [Required] public ResourceScopeRef? Scope { get; set; }
    public string Prefix { get; set; } = "";
    public string StorageKey { get; set; } = "";
    [Required] public string OperationKey { get; set; } = "";
    public string ServiceKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public JsonElement Parameters { get; set; } = JsonSerializer.SerializeToElement(new { });
    public Guid DocumentCollectionUid { get; set; }
    public bool LifecycleEnabled { get; set; } = true;
    public bool AllowDirect { get; set; }
    public bool KeepHistory { get; set; }
    public string AcquisitionTtl { get; set; } = "";
    public string HistoryRetention { get; set; } = "";
    public SchemaBinding? AcquisitionOutputBinding { get; set; }
    public SchemaBinding? OutputBinding { get; set; }
    public FlowCallReference? Orchestration { get; set; }
    public IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> CompositionPoints { get; set; } = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>();
    public bool NamespaceValid
    {
        get { try { _ = ResourceNamespace.Parse(Namespace); return true; } catch (ArgumentException) { return false; } }
    }
    public bool IdentityValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Trim().Length > 200 || Description?.Length > 2000) return false;
            try { _ = ResourceNamespace.Parse(Namespace); return true; }
            catch (ArgumentException) { return false; }
        }
    }
    public bool LifecycleValid => !LifecycleEnabled || LifecycleDuration.TryParse(AcquisitionTtl, out _) && (!KeepHistory
        || LifecycleDuration.TryParse(HistoryRetention, out var retention) && retention is not null)
        && (!string.IsNullOrEmpty(StorageKey) || string.IsNullOrWhiteSpace(AcquisitionTtl) && !KeepHistory);
    public DataSourceAcquisitionPolicy? LifecycleSelection()
    {
        if (!LifecycleEnabled && !AllowDirect) return null;
        _ = LifecycleDuration.TryParse(AcquisitionTtl, out var ttl);
        _ = LifecycleDuration.TryParse(HistoryRetention, out var retention);
        return new()
        {
            AllowDirect = AllowDirect,
            TimeToLive = LifecycleEnabled ? ttl : null,
            KeepHistory = LifecycleEnabled && KeepHistory,
            HistoryRetention = LifecycleEnabled && KeepHistory ? retention : null
        };
    }

    public bool CanSave(bool collectionValid, bool hasOperation, bool parametersValid, bool eligibleOperation, bool currentService) =>
        collectionValid && hasOperation && (AllowDirect || !string.IsNullOrEmpty(StorageKey)) && LifecycleValid
        && (!Enabled || parametersValid && eligibleOperation && currentService);

    public static DataSourceDatasetEditorModel From(DataSourceResource source, string serviceKey)
    {
        var saved = source.Definition.Dataset!;
        var compositions = saved.Compositions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (saved.Transformations.Count > 0) compositions["transformations"] = saved.Transformations;
        return new()
        {
            Name = source.Name,
            DisplayName = source.Definition.DisplayName,
            Description = source.Definition.Description,
            Namespace = source.Namespace.Value,
            Scope = source.ScopeRef,
            Enabled = source.Definition.Enabled,
            Tags = string.Join(Environment.NewLine, source.Metadata.Tags.Select(pair => $"{pair.Key}={pair.Value}")),
            LifecycleEnabled = saved.Lifecycle is not null,
            AllowDirect = saved.Lifecycle?.AllowDirect == true,
            AcquisitionTtl = LifecycleDuration.Format(saved.Lifecycle?.TimeToLive),
            KeepHistory = saved.Lifecycle?.KeepHistory == true,
            HistoryRetention = LifecycleDuration.Format(saved.Lifecycle?.HistoryRetention),
            AcquisitionOutputBinding = saved.Acquisition.OutputSchemaBinding,
            OutputBinding = saved.OutputSchemaBinding,
            Orchestration = saved.Orchestration,
            CompositionPoints = compositions,
            Prefix = saved.Rule.Prefix,
            Parameters = saved.Acquisition.Parameters.Clone(),
            DocumentCollectionUid = saved.DocumentCollectionUid ?? Guid.Empty,
            StorageKey = saved.Storage is { } storage ? $"{storage.StorageUid}:{storage.Generation}:{storage.BindingDigest}" : string.Empty,
            OperationKey = $"{saved.Acquisition.Operation.OperationUid}:{saved.Acquisition.Operation.Generation}:{saved.Acquisition.Operation.DefinitionDigest}",
            ServiceKey = serviceKey
        };
    }
}
