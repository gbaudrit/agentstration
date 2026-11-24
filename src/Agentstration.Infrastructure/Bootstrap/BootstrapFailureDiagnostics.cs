using Agentstration.Bootstrap.Contracts;
using Agentstration.DataSources;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Bootstrap;

// Persist only bounded reason codes and a correlation identity, never exception messages or manifests.
internal static class BootstrapFailureDiagnostics
{
    public static BootstrapFailure Describe(Exception exception)
    {
        var (category, code) = exception switch
        {
            DataSourceAcquisitionException value => ("dependency", value.Code),
            Agentstration.Storage.StorageException value => ("dependency", value.Code),
            Agentstration.Schemas.SchemaValidationException value => ("validation", value.Code),
            Agentstration.Knowledge.KnowledgeSourceValidationException value => ("validation", value.Code),
            DataSourceValidationException value => ("validation", value.CompositionErrorCode ?? value.Code),
            Agentstration.Tools.ToolDefinitionValidationException value => ("dependency", value.Code),
            Agentstration.Tools.ToolSetValidationException value => ("validation", value.Code),
            ModelProfileValidationException value => ("validation", value.Code),
            FlowValidationException value => ("validation", value.Code),
            ResourceGovernanceViolationException value => ("access", value.Code),
            AuthorizationDeniedException => ("access", "bootstrap_access_denied"),
            KeyNotFoundException => ("dependency", "bootstrap_dependency_missing"),
            TimeoutException value when value.Message == "bootstrap_discovery_timeout" => ("timeout", "bootstrap_discovery_timeout"),
            TimeoutException => ("timeout", "bootstrap_dependency_timeout"),
            IOException => ("storage", "bootstrap_storage_failed"),
            HttpRequestException => ("provider", "bootstrap_provider_unavailable"),
            Agentstration.ConnectedServices.ConnectedServiceOperationDiscoveryException value => ("dependency", value.Code),
            InvalidOperationException value when value.Message.StartsWith("bootstrap_", StringComparison.Ordinal)
                && value.Message.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') => ("configuration", value.Message),
            _ when exception.Message.StartsWith("[model_dependency_timeout]", StringComparison.Ordinal) => ("timeout", "model_dependency_timeout"),
            ArgumentException or InvalidOperationException => ("configuration", "bootstrap_configuration_invalid"),
            _ => ("unexpected", "bootstrap_resource_failed")
        };
        if (code.Length is 0 or > 128 || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
            code = "bootstrap_resource_failed";
        return new(category, code, Guid.NewGuid().ToString("N"));
    }
}
