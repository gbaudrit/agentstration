using Agentstration.Bootstrap.Contracts;

namespace Agentstration.Web.Console;

public interface IBootstrapStartupApiClient
{
    Task<BootstrapStartupAdmission> GetAdmissionAsync(CancellationToken cancellationToken);
    Task<BootstrapStartupSnapshot> GetAsync(CancellationToken cancellationToken);
}

public sealed class BootstrapStartupApiClient(HttpClient client) : IBootstrapStartupApiClient
{
    public Task<BootstrapStartupAdmission> GetAdmissionAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<BootstrapStartupAdmission>(client, "api/bootstrap/startup/admission", cancellationToken);
    public Task<BootstrapStartupSnapshot> GetAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<BootstrapStartupSnapshot>(client, "api/bootstrap/startup", cancellationToken);
}

public static class BootstrapStartupNavigation
{
    public const string ProgressPath = "/startup/progress";
    public static string LocalReturnUrl(string? value) => string.IsNullOrWhiteSpace(value) || !value.StartsWith('/')
        || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\') || value.Any(char.IsControl)
        || value.StartsWith("/startup", StringComparison.OrdinalIgnoreCase) || value.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("/logout", StringComparison.OrdinalIgnoreCase) ? "/" : value;
    public static string ProgressUrl(string? returnUrl) => ProgressPath + "?ReturnUrl=" + Uri.EscapeDataString(LocalReturnUrl(returnUrl));
    public static bool IsRepairPath(string path) => new[] { "/modelproviders", "/modelprofiles", "/extensions" }
        .Any(prefix => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
}
