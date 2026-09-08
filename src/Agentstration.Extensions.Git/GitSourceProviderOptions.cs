namespace Agentstration.Extensions.Git;

public sealed class GitSourceProviderOptions
{
    public string GitExecutable { get; set; } = "git";
    public string? TemporaryDirectory { get; set; }
    public bool AllowLocalRepositories { get; set; }
    public long MaximumRepositoryBytes { get; set; } = 512L * 1024 * 1024;
    public int ResolveTimeoutSeconds { get; set; } = 60;
}
