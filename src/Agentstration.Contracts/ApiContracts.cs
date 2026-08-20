using Agentstration.Domain;

namespace Agentstration.Contracts;

public sealed record CreateWorkspaceRequest(string Name);
public sealed record CreateInboxRequest(string Name, string? Slug, string? Description);
public sealed record InboxCreatedResponse(Inbox Inbox, string ApiKey);
public sealed record IngestItemRequest(string? Text, string? Url, string? ExternalId);
public sealed record IngestItemResponse(ItemId ItemId, string Status, bool Duplicate);
public sealed record ItemDetails(Item Item, RawContent Raw, NormalizedContent? Normalized, IReadOnlyList<ItemAnalysis> Analyses);
public sealed record CreateMissionRequest(string Name, string Objective, string SourceUrl, int FrequencyMinutes, decimal? Threshold);
public sealed record MissionDetails(Mission Mission, IReadOnlyList<MissionRun> Runs, IReadOnlyList<Notification> Notifications);
