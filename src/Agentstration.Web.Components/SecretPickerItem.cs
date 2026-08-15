namespace Agentstration.Web.Components;

public sealed record SecretPickerItem(string Name, string Namespace, string DisplayName, string Vault, string Status)
{
    public string Id => $"{Namespace}:{Name}";
}
