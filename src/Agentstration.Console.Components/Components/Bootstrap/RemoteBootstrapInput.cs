namespace Agentstration.Web.Components.Bootstrap;

/// <summary>Acquired input only; the host owns target selection, authorization and inspection.</summary>
public sealed record RemoteBootstrapInput(Guid RequestId, string? ArchiveBase64, string? Document, Uri? DocumentUrl);
