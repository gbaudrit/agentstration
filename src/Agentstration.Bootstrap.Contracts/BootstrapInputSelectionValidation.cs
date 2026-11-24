using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.Bootstrap.Contracts;

public static class BootstrapInputSelectionValidation
{
    public static void Validate(IReadOnlyList<BootstrapInputSelection>? inputs)
    {
        if (inputs is null) return;
        if (inputs.Count > 128 || inputs.Any(input => input is null
            || string.IsNullOrWhiteSpace(input.Profile) || input.Profile.Length > 128
            || string.IsNullOrWhiteSpace(input.Configuration) || input.Configuration.Length > 512
            || !ValidSource(input)) || inputs.GroupBy(input => (input.Profile, input.Configuration)).Any(group => group.Count() > 1))
            throw new BootstrapRequestException("bootstrap_input_selection_invalid");
    }

    private static bool ValidSource(BootstrapInputSelection input) =>
        input.Configuration.StartsWith("Agentstration:Bootstrap:Secrets:", StringComparison.OrdinalIgnoreCase)
            ? input.Kind == "secret" && input.Resource is { Kind: "Secret" } && input.Literal is null
            : input.Configuration.StartsWith("Agentstration:Bootstrap:Parameters:", StringComparison.OrdinalIgnoreCase)
                && (input.Kind == "literal" && input.Resource is null && input.Literal is not null
                    || input.Kind == "parameter" && input.Resource is { Kind: "Parameter" } && input.Literal is null);
}
