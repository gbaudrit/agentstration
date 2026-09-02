using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Work;

public static class DashboardIconDefaults
{
    public const string Grid = "layout-grid";
    public const string Home = "home";
}

public sealed record DashboardEntryReference
{
    public required EntryId EntryResourceId { get; init; }
    public DashboardItemRole Role { get; init; } = DashboardItemRole.Standard;
    public int Order { get; init; }
}

public sealed record WorkplaceDashboard
{
    public required DashboardId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = WorkResourceTypes.Dashboards;
    public string ApiVersion { get; init; } = WorkplaceApiVersions.CoreV1;
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public bool IsDefault { get; init; }
    public IReadOnlyList<DashboardEntryReference> Entries { get; init; } = [];
    public int Version { get; init; } = 1;
    public DateTimeOffset PublishedAt { get; init; }
}

public sealed record WorkplaceDashboardDraft
{
    public required DashboardId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = WorkResourceTypes.Dashboards;
    public string ApiVersion { get; init; } = WorkplaceApiVersions.CoreV1;
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public bool IsDefault { get; init; }
    public IReadOnlyList<DashboardEntryReference> Entries { get; init; } = [];
    public long Revision { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; }
}
