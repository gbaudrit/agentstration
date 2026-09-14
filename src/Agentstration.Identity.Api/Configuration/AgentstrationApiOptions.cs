namespace Agentstration.Web.Configuration;

public sealed class AgentstrationApiOptions
{
    public const string SectionName = "Agentstration";
    public ApiAuthenticationOptions Authentication { get; set; } = new();
}

public sealed class ApiAuthenticationOptions
{
    public const string Local = "Local";
    public const string Hybrid = "Hybrid";
    public const string Development = "Development";
    public const string Disabled = "Disabled";
    public const string Oidc = "Oidc";

    public string Mode { get; set; } = Local;
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
    public string? DataProtectionKeysPath { get; set; }
    public string DevelopmentIssuer { get; set; } = "https://agentstration.local/development";
    public string DevelopmentSubject { get; set; } = "development-operator";
    public string DevelopmentDisplayName { get; set; } = "Development operator";

    public static bool SupportsLocalAccounts(string mode) =>
        string.Equals(mode, Local, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Hybrid, StringComparison.OrdinalIgnoreCase);

    public static bool SupportsExternalLogin(string mode) =>
        string.Equals(mode, Oidc, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Hybrid, StringComparison.OrdinalIgnoreCase);
}
