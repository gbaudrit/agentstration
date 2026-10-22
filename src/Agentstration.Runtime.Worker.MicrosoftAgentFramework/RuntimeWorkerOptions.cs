namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

public sealed class RuntimeWorkerOptions
{
    public const string SectionName = "Agentstration:RuntimeWorker";

    public string AuthorityUrl { get; set; } = "http://localhost:5100";
    public Guid WorkerId { get; set; }
    public Guid CredentialId { get; set; }
    public string InstanceId { get; set; } = "local";
    public string SharedKeyFile { get; set; } = string.Empty;
    public string CredentialStateFile { get; set; } = string.Empty;
    public string PairingCodeFile { get; set; } = string.Empty;
    public string DisplayName { get; set; } = Environment.MachineName;
    public int MaximumConcurrentAssignments { get; set; } = 1;
    public int ClaimWaitSeconds { get; set; } = 20;
    public int TransientRetryCount { get; set; } = 3;
    public int LeaseSafetyMarginSeconds { get; set; } = 8;
    public bool AllowInsecureHttp { get; set; }

    public bool Validate()
    {
        if (!Uri.TryCreate(AuthorityUrl, UriKind.Absolute, out var authority)
            || (authority.Scheme != Uri.UriSchemeHttps && authority.Scheme != Uri.UriSchemeHttp)
            || (authority.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp)) return false;
        if (WorkerId == Guid.Empty || string.IsNullOrWhiteSpace(InstanceId) || string.IsNullOrWhiteSpace(DisplayName)) return false;
        if (MaximumConcurrentAssignments is < 1 or > 64 || ClaimWaitSeconds is < 1 or > 30
            || TransientRetryCount is < 0 or > 8 || LeaseSafetyMarginSeconds is < 1 or > 60) return false;
        var configuredCredential = CredentialId != Guid.Empty && !string.IsNullOrWhiteSpace(SharedKeyFile);
        var enrollmentCredential = !string.IsNullOrWhiteSpace(CredentialStateFile);
        return configuredCredential || enrollmentCredential;
    }
}
