using Agentstration.Sources.Contracts;

namespace Agentstration.Bootstrap.Contracts;

public sealed record BootstrapProfileFilesRequest(string Profile, string? File = null,
    BootstrapSourceProfileSelection? Source = null, string? ExpectedDigest = null);

public sealed record BootstrapProfileFiles(string Profile, string Digest, IReadOnlyList<string> Files,
    string? File = null, string? Yaml = null);
