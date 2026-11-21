using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.Web.Hosting;

public sealed record BootstrapInitialApplication
{
    public IReadOnlyList<string> Profiles { get; init; } = [];
    public string TargetLevel { get; init; } = "/instance";
}

internal enum BootstrapStartupPhase { All, Topology, Policies, Resources }

public sealed partial class DeclarativeBootstrapService
{
    private static bool IsTopology(string kind) => kind is
        "PlatformAdministrator" or "Tenant" or "Workspace" or "PrincipalDefaultContext";

    private static bool Includes(BootstrapStartupPhase phase, string kind) => phase switch
    {
        BootstrapStartupPhase.All => true,
        BootstrapStartupPhase.Topology => IsTopology(kind),
        BootstrapStartupPhase.Policies => kind == ExtensionKinds.ExtensionEnrollmentPolicy,
        BootstrapStartupPhase.Resources => !IsTopology(kind) && kind != ExtensionKinds.ExtensionEnrollmentPolicy,
        _ => false
    };

    internal void ValidateStartupProfiles(IReadOnlyList<LoadedBootstrapProfile> profiles)
    {
        if (profiles.Count == 0) throw new DeclarativeBootstrapException("An initial Bootstrap application requires profiles.");
        var scope = profiles[0].Summary.Scope;
        foreach (var profile in profiles)
        {
            if (profile.Summary.Scope != scope)
                throw new DeclarativeBootstrapException("Profiles in one initial application must have the same scope.");
            foreach (var source in profile.Resources)
            {
                if (!handlers.TryGetValue(source.Resource.Kind, out var handler))
                    throw new DeclarativeBootstrapException($"Unknown bootstrap resource kind '{source.Resource.Kind}' in '{source.Location}'.");
                if (!handler.SupportsProfileScope(scope))
                    throw new DeclarativeBootstrapException($"Resource '{source.Location}' is incompatible with profile scope '{scope}'.");
            }
            // Structural selector validation needs no existing target or extension.
            _ = BootstrapExtensionReferencePreparation.Requirements(profile,
                new(profile.Summary.Name, profile.DirectoryPath, BootstrapProfileScope.Instance), handlers, []);
        }
    }
}
