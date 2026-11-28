using System.Text.Json;
using Agentstration.Models;
using Agentstration.Resources;

namespace Agentstration.Extensions.Contracts;

public sealed record ExtensionConfigurationProperties(
    Guid RegistrationUid, string ExtensionId, string OptionSet, string Version, string SchemaDigest,
    IReadOnlyList<ModelProviderValueBinding> Bindings);

public sealed record ExtensionConfigurationObservation(long? AppliedGeneration, string? AppliedRevision, string State, string? Diagnostic = null);

public sealed record ExtensionConfigurationResource : Resource
{
    public required ExtensionConfigurationProperties Definition { get; init; }
    public ExtensionConfigurationObservation Observation { get; init; } = new(null, null, "pending");
}

public sealed record ExtensionConfigurationOption(
    string Id, JsonElement Schema, bool Required, bool Secured, IReadOnlyList<string> AllowedSources,
    string Scope, string ApplyMode, string? AtomicGroup);
public sealed record ExtensionConfigurationContract(string Id, string Version, string SchemaDigest,
    IReadOnlyList<ExtensionConfigurationOption> Options);
public sealed record ExtensionConfigurationView(
    bool Supported, ExtensionConfigurationContract? Contract, ExtensionConfigurationResource? Configuration,
    string? ETag, bool Ready, IReadOnlyDictionary<string, JsonElement> AppliedValues);
public sealed record PutExtensionConfigurationRequest(string OptionSet, string Version, string SchemaDigest,
    IReadOnlyList<ModelProviderValueBinding> Bindings)
{
    public string? Name { get; init; }
}

public interface IExtensionConfigurationService
{
    Task<ExtensionConfigurationView> GetAsync(ResourceNamespace resourceNamespace, string registrationName, CancellationToken cancellationToken);
    Task<ExtensionConfigurationView> PutAsync(ResourceNamespace resourceNamespace, string registrationName,
        PutExtensionConfigurationRequest request, string? ifMatch, CancellationToken cancellationToken);
    Task<ExtensionConfigurationView> ApplyAsync(ResourceNamespace resourceNamespace, string registrationName,
        string ifMatch, CancellationToken cancellationToken);
}

/// <summary>Revalidates the installation's mutable bindings before functional calls, in the owner's scope.</summary>
public interface IExtensionConfigurationSynchronizer
{
    Task EnsureAsync(ExtensionRegistrationResource registration, CancellationToken cancellationToken);
    Task EnsureAsync(ResourceReference registration, ResourceNamespace ownerNamespace, CancellationToken cancellationToken);
}

public sealed class ExtensionConfigurationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
