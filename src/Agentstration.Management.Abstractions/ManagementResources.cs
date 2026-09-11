using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Management.Abstractions;

public static class ManagementApiVersions
{
    public const string CoreV1 = ResourceApiVersions.CoreV1;
    public const string V20260801 = ResourceApiVersions.V20260801;
}
// Temporary source-compatibility surface for extensions compiled against the former store name.
// New code must depend on IResourceStore.
public interface IControlPlaneStore : IResourceStore { }

public static class ResourceKinds
{
    public const string Agent = "Agent";
    public const string AgentRevision = "AgentRevision";
    public const string AgentDeployment = "AgentDeployment";
    public const string Flow = "Flow";
    public const string Entry = "Entry";
    public const string ManagementOperation = "ManagementOperation";
    public const string InstalledPack = "InstalledPack";
    public const string PackConfiguration = "PackConfiguration";
    public const string ModelProvider = "ModelProvider";
    public const string SourceProvider = "SourceProvider";
    public const string ExtensionRegistration = "ExtensionRegistration";
    public const string AepEnrollmentSettings = "AepEnrollmentSettings";
    public const string AepEnrollmentRequest = "AepEnrollmentRequest";
    public const string ModelProfile = "ModelProfile";
    public const string RuntimeProfile = "RuntimeProfile";
    public const string Secret = "Secret";
    public const string Vault = "Vault";
    public const string Tool = "Tool";
    public const string ToolDefinition = "ToolDefinition";
    public const string ToolProvider = "ToolProvider";
    public const string ToolExecutionHook = "ToolExecutionHook";
    public const string Trigger = "Trigger";
    public const string BootstrapApplication = "BootstrapApplication";
    public const string Source = "Source";
    public const string SourceVersion = "SourceVersion";
    public const string SourceConfiguration = "SourceConfiguration";
    public const string SourceObservedState = "SourceObservedState";
    public const string SourceImportRecord = "SourceImportRecord";
    public const string SourceChannelSnapshot = "SourceChannelSnapshot";
    public const string SourceChannelObservedState = "SourceChannelObservedState";
    public const string SourceRegistryRegistration = "SourceRegistryRegistration";
    public const string SourceRegistryObservedState = "SourceRegistryObservedState";
    public const string SourceRegistryRefreshRecord = "SourceRegistryRefreshRecord";
    public const string SourceChannelRefreshRecord = "SourceChannelRefreshRecord";
}
