using System.Text.RegularExpressions;

namespace Agentstration.Console.Web.Security;

public sealed class BffWorkloadClientOptions
{
    public const string SectionName = "Agentstration:BffWorkload";

    public bool Enabled { get; set; }
    public string WorkloadId { get; set; } = string.Empty;
    public string CredentialId { get; set; } = string.Empty;
    public string SharedKeyFile { get; set; } = string.Empty;
    public string TargetInstanceId { get; set; } = string.Empty;

    public bool Validate() => !Enabled ||
        (ValidId(WorkloadId) && ValidId(CredentialId) && ValidId(TargetInstanceId) &&
         !string.IsNullOrWhiteSpace(SharedKeyFile));

    private static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, "^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant);
}
