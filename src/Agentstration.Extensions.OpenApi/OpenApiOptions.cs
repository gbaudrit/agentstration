using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.OpenApi;

public sealed record OpenApiOptions
{
    public const string SectionName = "OpenApi";
    public string[] AllowedDomains { get; set; } = [];
    public int[] AllowedPorts { get; set; } = [80, 443];
    public bool AllowPrivateAddresses { get; set; }
    public int MaximumCandidatePaths { get; set; } = 8;
    public int MaximumDocumentBytes { get; set; } = 2 * 1024 * 1024;
    public int MaximumResponseBytes { get; set; } = 4 * 1024 * 1024;
    public int MaximumOperations { get; set; } = 1_000;
    public int MaximumReferences { get; set; } = 256;
    public int MaximumReferenceDepth { get; set; } = 16;
    public int MaximumPages { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 30;
    public string[] AllowedMethods { get; set; } = ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"];
    public string[] AllowedContractMediaTypes { get; set; } =
        ["application/json", "application/yaml", "application/x-yaml", "text/yaml", "text/x-yaml"];
    public string[] AllowedResponseMediaTypes { get; set; } =
        ["application/json", "text/plain", "text/csv", "application/xml", "text/xml", "application/octet-stream"];
    public Dictionary<string, OpenApiCredentialOptions> Credentials { get; set; } = new(StringComparer.Ordinal);
}

public sealed class OpenApiCredentialOptions
{
    public string Scheme { get; set; } = "Bearer";
    public string Name { get; set; } = "Authorization";
    public string Value { get; set; } = string.Empty;
    public string Location { get; set; } = "header";
}

public sealed class OpenApiOptionsValidator : IValidateOptions<OpenApiOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenApiOptions options)
    {
        var errors = new List<string>();
        if (options.AllowedDomains.Length == 0) errors.Add("OpenApi:AllowedDomains must contain at least one domain.");
        if (options.AllowedPorts.Length == 0 || options.AllowedPorts.Any(value => value is < 1 or > 65535))
            errors.Add("OpenApi:AllowedPorts must contain valid TCP ports.");
        if (options.MaximumCandidatePaths is < 1 or > 32) errors.Add("OpenApi:MaximumCandidatePaths must be between 1 and 32.");
        if (options.MaximumDocumentBytes is < 1024 or > 16 * 1024 * 1024) errors.Add("OpenApi:MaximumDocumentBytes is outside the supported range.");
        if (options.MaximumResponseBytes is < 1024 or > 32 * 1024 * 1024) errors.Add("OpenApi:MaximumResponseBytes is outside the supported range.");
        if (options.MaximumOperations is < 1 or > 10_000) errors.Add("OpenApi:MaximumOperations is outside the supported range.");
        if (options.MaximumReferences is < 1 or > 2_000) errors.Add("OpenApi:MaximumReferences is outside the supported range.");
        if (options.MaximumReferenceDepth is < 1 or > 64) errors.Add("OpenApi:MaximumReferenceDepth is outside the supported range.");
        if (options.MaximumPages is < 1 or > 100) errors.Add("OpenApi:MaximumPages is outside the supported range.");
        if (options.TimeoutSeconds is < 1 or > 300) errors.Add("OpenApi:TimeoutSeconds is outside the supported range.");
        foreach (var credential in options.Credentials)
        {
            if (string.IsNullOrWhiteSpace(credential.Key) || credential.Key.Length > 128
                || string.IsNullOrWhiteSpace(credential.Value.Value) || credential.Value.Value.Length > 65_536)
                errors.Add("Configured OpenAPI credentials require a non-empty identity and value.");
            if (credential.Value.Location is not ("header" or "query"))
                errors.Add($"Credential '{credential.Key}' has an unsupported location.");
            if (string.IsNullOrWhiteSpace(credential.Value.Name) || credential.Value.Name.Length > 128
                || credential.Value.Name.Any(value => !(char.IsAsciiLetterOrDigit(value) || "!#$%&'*+-.^_`|~".Contains(value))))
                errors.Add($"Credential '{credential.Key}' has an invalid header or query name.");
            if (credential.Value.Value.Contains('\r') || credential.Value.Value.Contains('\n'))
                errors.Add($"Credential '{credential.Key}' contains an invalid line break.");
            if (credential.Value.Scheme is not ("Bearer" or "None"))
                errors.Add($"Credential '{credential.Key}' has an unsupported scheme.");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
