using System.Security.Claims;
using Agentstration.Aep.AspNetCore;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.OpenDal;

public sealed class OpenDalOptions
{
    public const string SectionName = "OpenDal";
    public string RootDirectory { get; set; } = "";
    public string[] AllowedClients { get; set; } = [];
    public bool AllowPairedClient { get; set; }
    public bool Enabled { get; set; } = true;

    public bool Authorizes(ClaimsPrincipal? caller) => Enabled && caller?.Identity?.IsAuthenticated == true
        && caller.HasClaim(AepAuthenticationDefaults.PermissionClaim, AepAuthenticationDefaults.InvokePermission)
        && (AllowedClients.Contains(caller.FindFirstValue(ClaimTypes.NameIdentifier), StringComparer.Ordinal)
            || AllowPairedClient && caller.HasClaim(AepAuthenticationDefaults.PairedClientClaim, "true"));
}

public sealed class OpenDalOptionsValidator(IConfiguration configuration) : IValidateOptions<OpenDalOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenDalOptions options)
    {
        if (options.AllowPairedClient && !string.Equals(configuration["Aep:EnrollmentMode"],
                nameof(AepEnrollmentMode.PairingCode), StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Fail("OpenDal:AllowPairedClient requires AEP PairingCode enrollment.");

        return Path.IsPathFullyQualified(options.RootDirectory)
            && options.AllowedClients is { Length: <= 32 }
            && options.AllowedClients.All(c => !string.IsNullOrWhiteSpace(c) && c.Length <= 128)
            && (options.AllowPairedClient || options.AllowedClients.Length > 0)
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail("OpenDal requires an absolute operator-owned RootDirectory and an authorized AEP client.");
    }
}
