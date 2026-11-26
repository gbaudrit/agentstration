using System.Text.Json;
using Agentstration.Aep.Abstractions;
using System.Text.Json.Serialization;
using Agentstration.Extensions.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Hosting;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class ExtensionConfigurationBootstrapHandler(IExtensionConfigurationService configurations) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private sealed record Definition(ResourceReference Extension, string OptionSet, string Version,
        IReadOnlyList<ModelProviderValueBinding> Bindings, string? SchemaDigest = null);
    public string Kind => ExtensionKinds.ExtensionConfiguration;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => profileScope == BootstrapProfileScope.Instance;
    public IReadOnlyList<string> ExtensionReferencePaths => ["extension"];

    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken)
    {
        var definition = Read(resource);
        var view = await configurations.GetAsync(definition.Extension.Namespace ?? resource.Metadata.Namespace,
            definition.Extension.Name, cancellationToken);
        Validate(definition, view);
        if (view.Configuration is not null) return new(BootstrapResourceDisposition.Skip);
        var identity = definition.Extension.ToString();
        if (planning.Contains(Kind, identity)) throw new DeclarativeBootstrapException("Several configurations select the same extension installation.");
        planning.Register(Kind, identity, document: resource);
        return new(BootstrapResourceDisposition.Create);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
    {
        var definition = Read(resource);
        var ns = definition.Extension.Namespace ?? resource.Metadata.Namespace;
        var view = await configurations.GetAsync(ns, definition.Extension.Name, cancellationToken);
        Validate(definition, view);
        if (view.Configuration is not null)
        {
            // Resolve the existing bindings again, preserving operator edits rather than replacing them with profile defaults.
            view = await configurations.ApplyAsync(ns, definition.Extension.Name, view.ETag!, cancellationToken);
            if (view.Configuration?.Observation.State != "applied") throw new DeclarativeBootstrapException("Extension configuration is pending activation.");
            return BootstrapResourceApplyResult.Skipped;
        }
        view = await configurations.PutAsync(ns, definition.Extension.Name,
            new(definition.OptionSet, definition.Version, view.Contract!.SchemaDigest, definition.Bindings) { Name = resource.Metadata.Name }, null, cancellationToken);
        if (view.Configuration?.Observation.State != "applied") throw new DeclarativeBootstrapException("Extension configuration is pending activation.");
        return BootstrapResourceApplyResult.Created;
    }

    private static Definition Read(BootstrapResourceDocument resource)
    {
        var value = resource.Definition.Deserialize<Definition>(JsonOptions)
            ?? throw new DeclarativeBootstrapException("An extension configuration definition is required.");
        if (value.Extension is null || value.Extension.ScopeRef != ResourceScopeRef.Instance || string.IsNullOrWhiteSpace(value.OptionSet)
            || string.IsNullOrWhiteSpace(value.Version) || value.Bindings is null || value.Bindings.Any(b => b is null) || value.Bindings.Count > 32)
            throw new DeclarativeBootstrapException("An exact Instance extension and a bounded versioned configuration are required.");
        return value;
    }

    private static void Validate(Definition definition, ExtensionConfigurationView view)
    {
        if (!view.Supported || view.Contract is not { } contract || contract.Id != definition.OptionSet || contract.Version != definition.Version
            || definition.SchemaDigest is { } digest && digest != contract.SchemaDigest)
            throw new DeclarativeBootstrapException("The extension configuration contract is unavailable or incompatible.");
        if (definition.Bindings.Count > contract.Options.Count
            || definition.Bindings.Any(b => !contract.Options.Any(o => o.Id == b.RequirementId)) || definition.Bindings.Select(b => b.RequirementId).Distinct(StringComparer.Ordinal).Count() != definition.Bindings.Count)
            throw new DeclarativeBootstrapException("Extension configuration bindings do not match the contract.");
        foreach (var option in contract.Options)
        {
            var binding = definition.Bindings.SingleOrDefault(b => b.RequirementId == option.Id);
            if (binding is null)
            {
                if (option.Required) throw new DeclarativeBootstrapException("A required extension option is missing.");
                continue;
            }
            if (option.Scope != "Process" || option.ApplyMode != "Immediate")
                throw new DeclarativeBootstrapException("This extension configuration apply mode is not supported yet.");
            if (binding.Kind == ModelProviderValueBindingKind.Literal && (binding.Literal is not { } literal
                || binding.Parameter is not null || binding.Secret is not null || AepOptionSchemaValidator.Validate(literal, option.Schema).Count != 0))
                throw new DeclarativeBootstrapException("An extension option literal does not match its contract.");
            if (!option.AllowedSources.Contains(binding.Kind.ToString(), StringComparer.Ordinal)
                || option.Secured && binding.Kind != ModelProviderValueBindingKind.Secret)
                throw new DeclarativeBootstrapException("An extension option source is not permitted.");
        }
    }
}
