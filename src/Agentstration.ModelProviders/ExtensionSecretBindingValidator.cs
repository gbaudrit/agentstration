using Agentstration.Aep.Abstractions;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.ModelProviders;

public sealed record ExtensionSecretBindingIssue(string Code, string Message, string? RequirementId = null);

public static class ExtensionSecretBindingValidator
{
    public static IReadOnlyList<ExtensionSecretBindingIssue> Validate(
        IReadOnlyList<SecretBinding> bindings,
        IReadOnlyList<AepValueRequirement>? requirements,
        string contributionKind,
        string contributionId,
        bool requireAll)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var issues = new List<ExtensionSecretBindingIssue>();
        var declared = (requirements ?? [])
            .Where(value => string.Equals(value.ContributionKind, contributionKind, StringComparison.Ordinal)
                && string.Equals(value.ContributionId, contributionId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(value => value.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.RequirementId))
            {
                issues.Add(new("secret_binding_invalid", "A Secret binding needs a requirement identifier."));
                continue;
            }
            if (!seen.Add(binding.RequirementId))
                issues.Add(new("secret_binding_duplicate", $"Value requirement '{binding.RequirementId}' is bound more than once.", binding.RequirementId));
            if (!declared.ContainsKey(binding.RequirementId))
                issues.Add(new("secret_binding_unknown", $"Value requirement '{binding.RequirementId}' is not declared by the extension contribution.", binding.RequirementId));
            if (binding.Secret is null
                || !string.Equals(binding.Secret.Address.Kind, SecretResourceKinds.Secret, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(binding.Secret.Address.Name)
                || binding.Secret.ScopeRef is not { } scopeRef
                || scopeRef == default)
                issues.Add(new("secret_binding_reference_invalid", $"Value requirement '{binding.RequirementId}' needs a scoped Secret reference.", binding.RequirementId));
        }
        if (requireAll)
        {
            foreach (var requirement in declared.Values.Where(value => value.Required && !seen.Contains(value.Id)))
                issues.Add(new("secret_binding_required", $"Required Value requirement '{requirement.Id}' has no binding.", requirement.Id));
        }
        return issues;
    }
}
