using System.Security.Claims;
using Agentstration.Aep.AspNetCore;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.OpenDal;

public sealed class OpenDalOptions
{
    public const string SectionName = "OpenDal";
    public string RootDirectory { get; set; } = "";
    public string[] AllowedClients { get; set; } = [];
    public bool Enabled { get; set; } = true;

    public bool Authorizes(ClaimsPrincipal? caller) => Enabled && caller?.Identity?.IsAuthenticated == true
        && caller.HasClaim(AepAuthenticationDefaults.PermissionClaim, AepAuthenticationDefaults.InvokePermission)
        && AllowedClients.Contains(caller.FindFirstValue(ClaimTypes.NameIdentifier), StringComparer.Ordinal);
}

public sealed class OpenDalOptionsValidator : IValidateOptions<OpenDalOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenDalOptions options) =>
        Path.IsPathFullyQualified(options.RootDirectory) && options.AllowedClients is { Length: > 0 and <= 32 }
        && options.AllowedClients.All(c => !string.IsNullOrWhiteSpace(c) && c.Length <= 128)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("OpenDal requires an absolute operator-owned RootDirectory and explicit AllowedClients.");
}
