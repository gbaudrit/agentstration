using System.Text.RegularExpressions;

namespace Agentstration.Identity.Api.Security;

public sealed class BffWorkloadTrustOptions
{
    public const string SectionName = "Agentstration:BffWorkloadTrust";
    public bool Enabled { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public int ReplayWindowSeconds { get; set; } = 120;
    public int MaximumBodyBytes { get; set; } = 65_536;
    public List<BffWorkloadCredentialOptions> Credentials { get; set; } = [];

    public bool Validate()
    {
        if (!Enabled) return true;
        if (!ValidId(InstanceId) || ReplayWindowSeconds is < 30 or > 300 || MaximumBodyBytes is < 1 or > 1_048_576)
            return false;
        if (Credentials.Count == 0 || Credentials.Any(credential => !credential.Validate())) return false;
        return Credentials.Select(credential => $"{credential.WorkloadId}\n{credential.CredentialId}")
            .Distinct(StringComparer.Ordinal).Count() == Credentials.Count;
    }

    internal static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant);
}

public sealed class BffWorkloadCredentialOptions
{
    public string WorkloadId { get; set; } = string.Empty;
    public string CredentialId { get; set; } = string.Empty;
    public string SharedKeyFile { get; set; } = string.Empty;
    public bool Revoked { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    internal bool Validate() =>
        BffWorkloadTrustOptions.ValidId(WorkloadId) &&
        BffWorkloadTrustOptions.ValidId(CredentialId) &&
        !string.IsNullOrWhiteSpace(SharedKeyFile) &&
        (NotBefore is null || ExpiresAt is null || NotBefore < ExpiresAt);
}
