namespace Agentstration.Web.Security;

/// <summary>Stable authorization policy names shared by the composed HTTP modules.</summary>
public static class AgentstrationPolicies
{
    public const string Authenticated = "agentstration:authenticated";
    public const string PlatformAdmin = "agentstration:platform-admin";
    public const string WorkspaceReader = "agentstration:workspace-reader";
    public const string WorkspaceAdmin = "agentstration:workspace-admin";
    public const string AuthorizationReader = "agentstration:authorization-reader";
    public const string AuthorizationAdmin = "agentstration:authorization-admin";
    public const string InteractiveUser = "agentstration:interactive-user";
    public const string CanReadResources = "agentstration:resources:read";
    public const string CanWriteResources = "agentstration:resources:write";
    public const string CanDeleteResources = "agentstration:resources:delete";
    public const string CanReadRuns = "agentstration:runs:read";
    public const string CanExecuteRuns = "agentstration:runs:execute";
    public const string CanDeleteRuns = "agentstration:runs:delete";
    public const string CanReadAgents = CanReadResources;
    public const string CanManageAgents = CanWriteResources;
    public const string CanRunAgents = CanExecuteRuns;
    public const string CanRunFlows = CanExecuteRuns;
}
