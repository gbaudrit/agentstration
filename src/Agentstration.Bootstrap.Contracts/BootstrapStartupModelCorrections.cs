using Agentstration.Resources;

namespace Agentstration.Bootstrap.Contracts;

public sealed class BootstrapStartupModelChangeException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record BootstrapStartupModelProviderChoice(Guid Uid, ResourceReference Reference, string DisplayName);
public sealed record BootstrapStartupModelOptions(
    Guid AttemptId, string ResourceId, long Version, ResourceReference Provider, string Model,
    IReadOnlyList<BootstrapStartupModelProviderChoice> Providers, IReadOnlyList<string> Models);
public sealed record BootstrapStartupModelChange(
    Guid AttemptId, long Version, ResourceReference Provider, Guid ProviderUid, string Model);
public sealed record BootstrapStartupModelChangeAccepted(Guid AttemptId, string ResourceId);
public sealed record BootstrapStartupModelSubstitution(
    ResourceReference PreviousProvider, string PreviousModel, ResourceReference Provider, string Model,
    Guid ActorPrincipalId, DateTimeOffset ChangedAt);

public interface IBootstrapStartupModelCorrections
{
    Task<BootstrapStartupModelOptions> GetOptionsAsync(Guid attemptId, string resourceId, Guid actorPrincipalId,
        string? providerName, string? providerNamespace, CancellationToken cancellationToken);
    Task ApplyAsync(string resourceId, BootstrapStartupModelChange request, Guid actorPrincipalId, CancellationToken cancellationToken);
}
