using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

public sealed record SourceBindingIssue(string Code, string Message, string? Channel = null);

public sealed record SourceBindingStatus(
    string Name,
    string TargetKind,
    ResourceReference? Target,
    string Status,
    IReadOnlyList<string> Channels,
    IReadOnlyList<SourceBindingIssue> Issues);

public sealed record SourceBindingStatusView(
    Guid SourceVersionUid,
    string SourceVersion,
    string ConfigurationETag,
    bool Ready,
    IReadOnlyList<SourceBindingStatus> Bindings,
    IReadOnlyList<SourceBindingSelection> StaleSelections);

public sealed record SourceBindingConfigurationResult(
    SourceConfigurationResource Configuration,
    SourceBindingStatusView Status);
