namespace Agentstration.Web.Components.Models;

public enum UiStatus { Neutral, Info, Success, Warning, Danger }

public sealed record EventListItem(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Type,
    string Summary,
    string? CorrelationId = null,
    string? Details = null,
    string? Url = null);

public sealed record TimelineItem(string Title, DateTimeOffset Timestamp, UiStatus Status, string? Detail = null);

public sealed record NotificationItem(Guid Id, string Title, string Message, DateTimeOffset Timestamp, UiStatus Status, bool IsRead = false);

