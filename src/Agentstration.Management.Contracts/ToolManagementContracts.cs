using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Management.Contracts;

public sealed record CreateToolProviderRequest(string Name, ToolProviderProperties Properties, ResourceScopeRef? ScopeRef = null);
public sealed record PutToolProviderRequest(ToolProviderProperties Properties);
public sealed record SetToolEnabledRequest(bool Enabled);
public sealed record ToolDiscoveryDiffResponse(int New, int Changed, int Unchanged, int Unavailable, int Total);
public sealed record ToolConnectionTestResponse(string Status, int ToolCount, IReadOnlyDictionary<string, bool> Capabilities, IReadOnlyDictionary<string, string> ServerMetadata);
public sealed record CreateToolDefinitionRequest(string Name, ToolDefinitionProperties Properties, string? Namespace = null);
public sealed record PutToolDefinitionRequest(ToolDefinitionProperties Properties);
public sealed record SetToolDefinitionEnabledRequest(bool Enabled);
public sealed record CreateToolExecutionHookRequest(string Name, ToolExecutionHookProperties Properties, string? Namespace = null);
public sealed record PutToolExecutionHookRequest(ToolExecutionHookProperties Properties);
