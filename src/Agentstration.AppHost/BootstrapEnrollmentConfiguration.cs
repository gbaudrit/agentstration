using Microsoft.Extensions.Configuration;

namespace Agentstration.AppHost;

public static class BootstrapEnrollmentConfiguration
{
    public static IReadOnlyDictionary<string, string> CreateEnvironment(IConfiguration configuration)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in new[]
        {
            "Agentstration:Bootstrap",
            "Agentstration:Aep:Enrollment:PairingCodeAllowed",
            "Agentstration:Aep:Enrollment:SharedKeyFileAllowed",
            "Agentstration:Aep:Enrollment:AutoEnrollmentLevel"
        })
            foreach (var value in configuration.GetSection(root).AsEnumerable())
                if (value.Value is not null && !value.Key.StartsWith("Agentstration:Bootstrap:Secrets:", StringComparison.OrdinalIgnoreCase))
                    result[value.Key.Replace(":", "__", StringComparison.Ordinal)] = value.Value;

        // Only trust metadata and references to orchestrator-owned files are
        // forwarded. Paths and activation of the initial catalog remain resolved
        // by AppHost's existing launch-profile handling.
        foreach (var value in configuration.GetSection("Agentstration:Extensions").AsEnumerable())
        {
            if (value.Value is null) continue;
            if (value.Key.EndsWith(":SharedKeyFile:Path", StringComparison.OrdinalIgnoreCase)
                || value.Key.EndsWith(":EnrollmentMode", StringComparison.OrdinalIgnoreCase)
                || value.Key.EndsWith(":RegistrationName", StringComparison.OrdinalIgnoreCase)
                || value.Key.EndsWith(":DisplayName", StringComparison.OrdinalIgnoreCase))
                result[value.Key.Replace(":", "__", StringComparison.Ordinal)] = value.Value;
        }
        return result;
    }

    public static IReadOnlyDictionary<string, string> SecretEnvironment(IConfiguration configuration) =>
        configuration.GetSection("Agentstration:Bootstrap:Secrets").AsEnumerable()
            .Where(value => value.Value is not null)
            .ToDictionary(value => value.Key.Replace(":", "__", StringComparison.Ordinal), value => value.Value!, StringComparer.Ordinal);
}
