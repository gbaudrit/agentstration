using System.Reflection;
using Agentstration.Management.Abstractions;

namespace Agentstration.Infrastructure.Sources;

public sealed class AssemblyAgentstrationVersionProvider : IAgentstrationVersionProvider
{
    public string? CurrentVersion { get; } = typeof(AssemblyAgentstrationVersionProvider).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion;
}
