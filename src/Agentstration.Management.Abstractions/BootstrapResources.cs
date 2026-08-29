using System.Text.Json;

namespace Agentstration.Management.Abstractions;

public static class BootstrapResourceKinds
{
    public const string PlatformAdministrator = "PlatformAdministrator";
    public const string Tenant = "Tenant";
    public const string Workspace = "Workspace";
    public const string PrincipalDefaultContext = "PrincipalDefaultContext";
}

public sealed record BootstrapResourceDocument
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public ResourceMetadata Metadata { get; init; } = new();
    public JsonElement Definition { get; init; }
}

public enum BootstrapResourceApplyResult
{
    Created,
    Skipped,
    Conflict
}

public interface IBootstrapResourceHandler
{
    string Kind { get; }
    Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, CancellationToken cancellationToken);
}
