namespace Agentstration.Web.Configuration;

public sealed class AgentstrationWebOptions
{
    public const string SectionName = "Agentstration";
    public ApiEndpointOptions ManagementApi { get; set; } = new();
    public ApiEndpointOptions RuntimeApi { get; set; } = new();
    public ApiEndpointOptions WorkApi { get; set; } = new();
    public ApiEndpointOptions FlowApi { get; set; } = new();
    public string? WorkplaceBaseUrl { get; set; }
    public AuthenticationOptions Authentication { get; set; } = new();
}

public sealed class ApiEndpointOptions
{
    public string BaseAddress { get; set; } = "http://localhost:5100/";
    public int TimeoutSeconds { get; set; } = 15;
    public bool ForwardSessionCookie { get; set; }
}

public sealed class AuthenticationOptions
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
    public string DevelopmentIssuer { get; set; } = Agentstration.Management.Core.LocalBootstrapOptions.DevelopmentIssuer;
    public string DevelopmentSubject { get; set; } = Agentstration.Management.Core.LocalBootstrapOptions.DevelopmentSubject;
    public string DevelopmentDisplayName { get; set; } = "Local operator";

    public static bool SupportsLocalAccounts(string mode) =>
        string.Equals(mode, Local, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Hybrid, StringComparison.OrdinalIgnoreCase);

    public static bool SupportsExternalLogin(string mode) =>
        string.Equals(mode, Oidc, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Hybrid, StringComparison.OrdinalIgnoreCase);
}
