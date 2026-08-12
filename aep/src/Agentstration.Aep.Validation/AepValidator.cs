using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;

namespace Agentstration.Aep.Validation;

public enum AepValidationSeverity { Information, Warning, Error }

public sealed record AepValidationIssue(string Code, string Message, AepValidationSeverity Severity, string? Target = null);

public sealed record AepValidationResult(bool IsValid, IReadOnlyCollection<AepValidationIssue> Issues);

public interface IAepValidator
{
    Task<AepValidationResult> ValidateAsync(IAepClient client, CancellationToken cancellationToken = default);
}

public sealed class AepValidator : IAepValidator
{
    public async Task<AepValidationResult> ValidateAsync(IAepClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var issues = new List<AepValidationIssue>();
        AepManifest manifest;
        try { manifest = await client.GetManifestAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            issues.Add(new("AEP001", $"Manifest request failed: {exception.Message}", AepValidationSeverity.Error, AepProtocol.DiscoveryPath));
            return new(false, issues);
        }

        if (string.IsNullOrWhiteSpace(manifest.ProtocolVersion)) issues.Add(new("AEP002", "Protocol version is required.", AepValidationSeverity.Error, "protocolVersion"));
        else if (!string.Equals(manifest.ProtocolVersion, AepProtocol.Version, StringComparison.Ordinal)) issues.Add(new("AEP003", $"Protocol version '{manifest.ProtocolVersion}' is not supported by this validator.", AepValidationSeverity.Error, "protocolVersion"));
        if (string.IsNullOrWhiteSpace(manifest.Extension.Id)) issues.Add(new("AEP004", "Extension id is required.", AepValidationSeverity.Error, "extension.id"));
        if (string.IsNullOrWhiteSpace(manifest.Extension.Name)) issues.Add(new("AEP005", "Extension name is required.", AepValidationSeverity.Error, "extension.name"));
        if (string.IsNullOrWhiteSpace(manifest.Extension.Version)) issues.Add(new("AEP006", "Extension version is required.", AepValidationSeverity.Error, "extension.version"));
        foreach (var capability in manifest.Capabilities)
        {
            if (!capability.Key.StartsWith("aep.", StringComparison.Ordinal)) issues.Add(new("AEP020", $"Capability '{capability.Key}' is outside the AEP namespace.", AepValidationSeverity.Warning, $"capabilities.{capability.Key}"));
            if (string.IsNullOrWhiteSpace(capability.Value.Version)) issues.Add(new("AEP021", $"Capability '{capability.Key}' has no version.", AepValidationSeverity.Error, $"capabilities.{capability.Key}.version"));
        }
        foreach (var descriptorIssue in AepDescriptorValidator.Validate(manifest)) issues.Add(new("AEP100", descriptorIssue, AepValidationSeverity.Error, "contributions.tools"));
        try
        {
            var health = await client.GetHealthAsync(cancellationToken);
            if (!string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase)) issues.Add(new("AEP010", $"Extension health is '{health.Status}'.", AepValidationSeverity.Warning, AepProtocol.HealthPath));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            issues.Add(new("AEP011", $"Health request failed: {exception.Message}", AepValidationSeverity.Error, AepProtocol.HealthPath));
        }
        return new(!issues.Any(value => value.Severity == AepValidationSeverity.Error), issues);
    }
}
