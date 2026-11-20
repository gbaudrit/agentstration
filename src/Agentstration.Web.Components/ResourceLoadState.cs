namespace Agentstration.Web.Components;

public enum ResourceLoadStatus { Ready, Loading, Refreshing, Failed, NotFound, Forbidden }

/// <summary>Presentation state only; the resource screen owns loading, authorization and retry commands.</summary>
public sealed record ResourceLoadState(ResourceLoadStatus Status)
{
    public bool HasContent { get; init; }
    public string? Message { get; init; }
    public string? ErrorId { get; init; }
    public bool BlocksContent => Status is ResourceLoadStatus.Loading or ResourceLoadStatus.NotFound or ResourceLoadStatus.Forbidden
        || Status == ResourceLoadStatus.Failed && !HasContent;
    public bool CanRetry => Status == ResourceLoadStatus.Failed;
}
