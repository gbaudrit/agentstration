namespace Agentstration.Web.Configuration;

public sealed class AgentstrationWebOptions
{
    public const string SectionName = "Agentstration";
    public bool UseSimulatedData { get; set; } = true;
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
}

public sealed class AuthenticationOptions
{
    public bool DevelopmentMode { get; set; } = true;
}
