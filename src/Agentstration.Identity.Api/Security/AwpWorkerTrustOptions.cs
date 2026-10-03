using System.Text.RegularExpressions;

namespace Agentstration.Identity.Api.Security;

public sealed class AwpWorkerTrustOptions
{
    public const string SectionName = "Agentstration:AwpWorkerTrust";
    public bool Enabled { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public int ReplayWindowSeconds { get; set; } = 300;
    public int MaximumBodyBytes { get; set; } = 2_097_152;
    public List<AwpWorkerCredentialOptions> Credentials { get; set; } = [];

    public bool Validate()
    {
        if (!Enabled) return true;
        if (!ValidId(InstanceId) || ReplayWindowSeconds is < 30 or > 900 || MaximumBodyBytes is < 1 or > 4_194_304)
            return false;
        return Credentials.All(value => value.Validate())
            && Credentials.Select(value => $"{value.WorkerId:D}\n{value.CredentialId:D}").Distinct(StringComparer.Ordinal).Count() == Credentials.Count;
    }

    private static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant);
}

public sealed class AwpWorkerCredentialOptions
{
    public Guid WorkerId { get; set; }
    public Guid CredentialId { get; set; }
    public string SharedKeyFile { get; set; } = string.Empty;
    public bool Revoked { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    internal bool Validate() => WorkerId != Guid.Empty && CredentialId != Guid.Empty
        && !string.IsNullOrWhiteSpace(SharedKeyFile)
        && (NotBefore is null || ExpiresAt is null || NotBefore < ExpiresAt);
}
