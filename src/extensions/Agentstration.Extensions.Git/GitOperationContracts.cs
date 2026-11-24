using System.Text.Json;

namespace Agentstration.Extensions.Git;

public sealed class GitOperationsOptions
{
    public bool Enabled { get; set; }
    public string[] AllowedRepositories { get; set; } = [];
    public bool AllowHttpLoopback { get; set; }
    public string StateDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "git-operations");
    public int TimeoutSeconds { get; set; } = 25;
}

public static class GitOperationProtocol
{
    public const string MetadataKey = "agentstration.git.repository";
    public const string Version = "1.0";
}

public sealed record GitFileChange(string Path, string? ContentBase64, bool? Executable = null);
public sealed record GitRepositoryInput(string RefName, string? Revision = null, string? BaseRevision = null,
    string? ExpectedRefRevision = null, IReadOnlyList<string>? Paths = null, IReadOnlyList<GitFileChange>? Changes = null, string? Message = null);
public sealed record GitRepositoryFile(string Path, string Revision, string ContentBase64, bool Executable);
public sealed record GitRepositoryResponse(string OperationKey, string IntentDigest, string Status, string RefName,
    string? Revision = null, string? BaseRevision = null, string? CurrentRevision = null,
    IReadOnlyList<GitRepositoryFile>? Files = null, string? DiffBase64 = null, string? ErrorCode = null);

internal sealed record GitPublicationIntent(string Authority, string IntentDigest, JsonElement Input, long Timestamp,
    string Phase, string? Candidate = null, GitRepositoryResponse? Result = null);
public sealed class GitOperationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
internal sealed record GitCommandResult(int ExitCode, byte[] Output)
{
    public string Text => System.Text.Encoding.UTF8.GetString(Output);
}
