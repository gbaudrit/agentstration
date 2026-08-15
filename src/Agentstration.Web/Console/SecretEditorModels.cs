using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public sealed class VaultEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")] public string Name { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    [Required] public string ProviderType { get; set; } = "local";
    public VaultProperties Properties() => new() { DisplayName = DisplayName.Trim(), ProviderType = ProviderType.Trim().ToLowerInvariant() };
    public static VaultEditorModel From(VaultResource value) => new() { Name = value.Name, DisplayName = value.Definition.DisplayName, ProviderType = value.Definition.ProviderType };
}

public sealed class SecretEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")] public string Name { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    [Required] public string VaultName { get; set; } = string.Empty;
    [Required] public string Key { get; set; } = string.Empty;
    public SecretProperties Properties() => new() { DisplayName = DisplayName.Trim(), Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), Vault = new ResourceReference(VaultName), Key = Key.Trim(), SecretType = SecretType.Opaque };
    public static SecretEditorModel From(SecretResource value) => new() { Name = value.Name, DisplayName = value.Definition.DisplayName, Description = value.Definition.Description, VaultName = value.Definition.Vault.Name, Key = value.Definition.Key };

    public static string IdentifierFromDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(Math.Min(normalized.Length, 128));
        var separatorPending = false;
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (separatorPending && result.Length > 0 && result.Length < 128) result.Append('-');
                if (result.Length < 128) result.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else separatorPending = result.Length > 0;
            if (result.Length == 128) break;
        }
        return result.ToString().TrimEnd('-');
    }
}
