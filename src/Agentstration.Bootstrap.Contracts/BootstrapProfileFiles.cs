using Agentstration.Sources.Contracts;

namespace Agentstration.Bootstrap.Contracts;

public sealed record BootstrapProfileFilesRequest(string Profile, string? File = null,
    BootstrapSourceProfileSelection? Source = null, string? ExpectedDigest = null)
{
    public WorkspaceSeedSelection? Seed { get; init; }
    public Agentstration.ResourceManagement.Contracts.BootstrapApplicationTarget? Target { get; init; }
}

public sealed record BootstrapProfileFiles(string Profile, string Digest, IReadOnlyList<string> Files,
    string? File = null, string? Yaml = null);
