namespace Agentstration.Aep.Client;

/// <summary>Resolves the workload credential used for one AEP request.</summary>
public interface IAepAccessTokenProvider
{
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Provides one pre-provisioned opaque Bearer credential.</summary>
public sealed class StaticAepAccessTokenProvider(string accessToken) : IAepAccessTokenProvider
{
    private readonly string _accessToken = !string.IsNullOrWhiteSpace(accessToken)
        ? accessToken
        : throw new ArgumentException("An AEP access token is required.", nameof(accessToken));

    public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(_accessToken);
}
