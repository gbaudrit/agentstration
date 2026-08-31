using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed partial class PackManagementService
{
    private void ValidateManifest(PackArchive archive)
    {
        var manifest = archive.Manifest;
        if (manifest.ApiVersion != ManagementApiVersions.CoreV1)
            throw new PackValidationException("pack_api_version_unsupported", $"Supported Pack apiVersion is '{ManagementApiVersions.CoreV1}'.");
        if (manifest.Kind != PackKinds.Pack) throw new PackValidationException("pack_kind_invalid", $"Pack manifest kind must be '{PackKinds.Pack}'.");
        ValidateName(manifest.Metadata.Publisher, "publisher");
        ValidateName(manifest.Metadata.Name, "name");
        if (!Enum.IsDefined(manifest.Metadata.Audience))
            throw new PackValidationException("pack_audience_invalid", "Pack audience must be universal, personal, or professional.");
        if (!Enum.IsDefined(manifest.Metadata.Purpose))
            throw new PackValidationException("pack_purpose_invalid", "Pack purpose must be sample, template, or standard.");
        if (!SemanticVersionRegex().IsMatch(manifest.Metadata.Version))
            throw new PackValidationException("pack_version_invalid", "Pack version must use Semantic Versioning.");
        if (manifest.Definition.Requirements.Count > 0)
            throw new PackValidationException("pack_requirements_unsupported", "Pack requirements are declared but dependency resolution is not available in V1.");
        var duplicateBinding = manifest.Definition.Bindings.GroupBy(binding => binding.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateBinding is not null)
            throw new PackValidationException("pack_binding_duplicate", $"Pack binding '{duplicateBinding.Key}' is declared more than once.");
        foreach (var binding in manifest.Definition.Bindings)
        {
            ValidateBindingName(binding.Name);
            if (!Enum.IsDefined(binding.TargetKind))
                throw new PackValidationException("pack_binding_kind_invalid", $"Pack binding '{binding.Name}' has an unsupported target kind.");
        }
        var referencedBindings = archive.Resources.SelectMany(resource => BindingNames(resource.Manifest)).ToArray();
        var undeclaredBinding = referencedBindings.FirstOrDefault(name => !manifest.Definition.Bindings.Any(binding => string.Equals(binding.Name, name, StringComparison.Ordinal)));
        if (undeclaredBinding is not null)
            throw new PackValidationException("pack_binding_reference_unknown", $"Resource references undeclared Pack binding '{undeclaredBinding}'.");
        var unusedBinding = manifest.Definition.Bindings.FirstOrDefault(binding => !referencedBindings.Contains(binding.Name, StringComparer.Ordinal));
        if (unusedBinding is not null)
            throw new PackValidationException("pack_binding_unused", $"Pack binding '{unusedBinding.Name}' is not referenced by a contained resource.");
        if (manifest.Definition.Resources.Count != archive.Resources.Count)
            throw new PackValidationException("pack_resource_count_mismatch", "The Pack resource list does not match the validated archive content.");
        if (!manifest.Definition.Resources.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(archive.Resources.Select(value => value.Path)))
            throw new PackValidationException("pack_resource_path_mismatch", "The Pack resource paths do not match the validated archive content.");
        var duplicates = archive.Resources.GroupBy(value => (value.Kind, value.Name)).FirstOrDefault(value => value.Count() > 1);
        if (duplicates is not null)
            throw new PackValidationException("pack_resource_duplicate", $"Resource '{duplicates.Key.Kind}/{duplicates.Key.Name}' is declared more than once.");
    }

    private static void ValidateName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 60 || !char.IsAsciiLetterOrDigit(value[0]) || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-') || value.Any(char.IsUpper))
            throw new PackValidationException($"pack_{field}_invalid", $"Pack {field} must contain 1 to 60 lowercase ASCII letters, digits or '-' and start with a letter or digit.");
    }

    private static void ValidateBindingName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.'))
            throw new PackValidationException("pack_binding_name_invalid", "Pack binding names must contain 1 to 128 ASCII letters, digits, '-' or '.' and start with a letter or digit.");
    }

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();
}
