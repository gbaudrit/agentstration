using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Core;

public sealed record ModelProviderView(ModelProviderConfiguration Configuration, ModelProviderHealth Health, IReadOnlyList<DiscoveredModel> Models, DateTimeOffset CheckedAt);
public sealed record ModelProfileUsage(string Kind, string Name, string DisplayName);
public sealed record ModelProviderUsage(string Kind, string Name, string DisplayName);
public sealed record ModelProfileResolution(
    ModelProfileResource Profile,
    ModelProviderConfiguration? Provider,
    ModelProviderHealth ProviderHealth,
    DiscoveredModel? Model,
    string Status,
    IReadOnlyList<string> Warnings,
    ResolvedModelProviderCapabilities? CapabilityLevels = null,
    EffectiveCapabilities? EffectiveCapabilities = null,
    IReadOnlyList<ExecutionCapabilityIssue>? Incompatibilities = null);

public sealed class ModelProfileValidationException(string code, string message, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}
public sealed class ModelProfileInUseException(string profileName, IReadOnlyList<ModelProfileUsage> usages) : Exception($"The model profile '{profileName}' is used by {usages.Count} agent(s).")
{
    public string ProfileName { get; } = profileName;
    public IReadOnlyList<ModelProfileUsage> Usages { get; } = usages;
}
public sealed class ModelProviderUnavailableException(string providerName, string? details) : Exception($"Model provider '{providerName}' is unavailable{(string.IsNullOrWhiteSpace(details) ? "." : $": {details}")}");
public sealed class ModelProviderResourceNotFoundException(string providerName) : Exception($"Model provider '{providerName}' was not found.");
public sealed class ModelProviderValidationException(string message) : Exception(message);
public sealed class ModelProviderInUseException(string providerName, IReadOnlyList<ModelProviderUsage> usages) : Exception($"The model provider '{providerName}' is used by {usages.Count} model profile(s).")
{
    public IReadOnlyList<ModelProviderUsage> Usages { get; } = usages;
}
