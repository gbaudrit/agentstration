using Agentstration.Aep.Abstractions;
using Agentstration.Models;
using Agentstration.Parameters;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.ModelProviders;

public sealed record ModelProviderValueBindingIssue(string Code, string Message, string? RequirementId = null);

public static class ModelProviderValueBindingValidator
{
    public static IReadOnlyList<ModelProviderValueBindingIssue> Validate(
        IReadOnlyList<ModelProviderValueBinding>? bindings,
        IReadOnlyList<AepValueRequirement>? requirements,
        string contributionId,
        bool requireAll)
    {
        var issues = new List<ModelProviderValueBindingIssue>();
        var declared = new Dictionary<string, AepValueRequirement>(StringComparer.Ordinal);
        foreach (var requirement in (requirements ?? []).Where(value =>
                     string.Equals(value.ContributionKind, AepContributionKinds.ModelProvider, StringComparison.Ordinal)
                     && string.Equals(value.ContributionId, contributionId, StringComparison.OrdinalIgnoreCase)))
        {
            if (!declared.TryAdd(requirement.Id, requirement))
                issues.Add(new("value_requirement_duplicate", $"Value requirement '{requirement.Id}' is declared more than once.", requirement.Id));
        }
        var supplied = bindings ?? [];
        if (supplied.Count > AepBoundValueValidator.MaximumBoundValues)
            issues.Add(new("value_bindings_too_many", $"A Model Provider may bind at most {AepBoundValueValidator.MaximumBoundValues} values."));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in supplied.Take(AepBoundValueValidator.MaximumBoundValues))
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.RequirementId))
            {
                issues.Add(new("value_binding_invalid", "A Value Binding needs a requirement identifier."));
                continue;
            }
            if (!seen.Add(binding.RequirementId))
                issues.Add(new("value_binding_duplicate", $"Value requirement '{binding.RequirementId}' is bound more than once.", binding.RequirementId));
            if (!declared.TryGetValue(binding.RequirementId, out var requirement))
            {
                issues.Add(new("value_binding_unknown", $"Value requirement '{binding.RequirementId}' is not declared by the Model Provider contribution.", binding.RequirementId));
                continue;
            }
            switch (binding.Kind)
            {
                case ModelProviderValueBindingKind.Parameter:
                    if (!ValidParameter(binding.Parameter) || binding.Secret is not null)
                        issues.Add(new("value_binding_reference_invalid", $"Value requirement '{binding.RequirementId}' needs one exact scoped Parameter reference.", binding.RequirementId));
                    else if (requirement.Protection == AepValueProtection.Secured)
                        issues.Add(new("value_binding_protection_invalid", $"Secured Value requirement '{binding.RequirementId}' must bind a Secret.", binding.RequirementId));
                    break;
                case ModelProviderValueBindingKind.Secret:
                    if (!ValidSecret(binding.Secret) || binding.Parameter is not null)
                        issues.Add(new("value_binding_reference_invalid", $"Value requirement '{binding.RequirementId}' needs one exact scoped Secret reference.", binding.RequirementId));
                    break;
                default:
                    issues.Add(new("value_binding_kind_invalid", $"Value requirement '{binding.RequirementId}' has an unsupported binding kind.", binding.RequirementId));
                    break;
            }
        }
        if (requireAll)
        {
            foreach (var requirement in declared.Values.Where(value => value.Required && !seen.Contains(value.Id)))
                issues.Add(new("value_binding_required", $"Required Value requirement '{requirement.Id}' has no binding.", requirement.Id));
        }
        return issues;
    }

    public static IReadOnlyList<ModelProviderValueBinding> Merge(
        IReadOnlyList<ModelProviderValueBinding> providerBindings,
        IReadOnlyList<SecretBinding> profileOverrides)
    {
        var merged = providerBindings.ToDictionary(value => value.RequirementId, StringComparer.Ordinal);
        foreach (var binding in profileOverrides)
            merged[binding.RequirementId] = ModelProviderValueBinding.FromSecret(binding.RequirementId, binding.Secret);
        return merged.Values.ToArray();
    }

    private static bool ValidParameter(ParameterReference? reference) => reference is not null
        && reference.ScopeRef != default
        && string.Equals(reference.Address.Kind, ParameterResourceKinds.Parameter, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(reference.Address.Name);

    private static bool ValidSecret(SecretReference? reference) => reference is not null
        && reference.ScopeRef is { } scopeRef
        && scopeRef != default
        && string.Equals(reference.Address.Kind, SecretResourceKinds.Secret, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(reference.Address.Name);
}
