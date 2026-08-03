using Agentstration.Management.Abstractions;

namespace Agentstration.Runtime.Abstractions;

public sealed record ExecutionCapabilityIssue(
    string Capability,
    string Provider,
    string Model,
    string Runtime,
    CapabilitySupport EffectiveSupport,
    string Message);

public sealed class ExecutionCompatibilityException(IReadOnlyList<ExecutionCapabilityIssue> issues)
    : Exception(string.Join(" ", issues.Select(issue => issue.Message)))
{
    public IReadOnlyList<ExecutionCapabilityIssue> Issues { get; } = issues;
}

public static class ExecutionCompatibilityValidator
{
    public static void Validate(
        ModelProfileProperties profile,
        ModelExecutionOptions execution,
        EffectiveCapabilities capabilities,
        string provider,
        string model,
        string runtime,
        bool toolsRequested = false,
        string? endpointMode = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(capabilities);
        var issues = new List<ExecutionCapabilityIssue>();
        if (profile.Reasoning.Mode == ReasoningMode.Enabled)
        {
            Require("reasoning", capabilities.Reasoning.Support, "Reasoning was requested but is not supported by the effective execution chain.", issues);
            if (profile.Reasoning.Effort is { } effort
                && capabilities.Reasoning.SupportedEfforts.Count > 0
                && !capabilities.Reasoning.SupportedEfforts.Contains(effort))
                Add("reasoning.effort", capabilities.Reasoning.Support, $"Reasoning effort '{effort}' is not supported by the effective execution chain.", issues);
        }
        if (profile.Output.Format != ModelOutputFormat.Text)
            Require("structuredOutput", capabilities.StructuredOutput.Support, "Structured output was requested but is not supported by the effective execution chain.", issues);
        if (profile.Output.Strict && capabilities.StructuredOutput.Support is CapabilitySupport.Unsupported or CapabilitySupport.Partial)
            Add("structuredOutput.strict", capabilities.StructuredOutput.Support, "Strict structured output requires full effective support.", issues);
        if (toolsRequested) Require("tools", capabilities.Tools.Support, "Tool calling was requested but is not supported by the effective execution chain.", issues);
        if (execution.Streaming == StreamingMode.Enabled)
            Require("streaming", capabilities.Streaming.Support, "Streaming was requested but is not supported by the effective execution chain.", issues);
        if (string.Equals(runtime, "microsoft-agent-framework", StringComparison.OrdinalIgnoreCase)
            && string.Equals(endpointMode, "generate", StringComparison.OrdinalIgnoreCase))
            Add("endpointMode", CapabilitySupport.Unsupported, "Ollama endpointMode 'generate' is incompatible with the Microsoft Agent Framework runtime.", issues);
        if (issues.Count > 0) throw new ExecutionCompatibilityException(issues);

        void Require(string capability, CapabilitySupport support, string message, ICollection<ExecutionCapabilityIssue> target)
        {
            if (support == CapabilitySupport.Unsupported) Add(capability, support, message, target);
        }

        void Add(string capability, CapabilitySupport support, string message, ICollection<ExecutionCapabilityIssue> target) =>
            target.Add(new ExecutionCapabilityIssue(capability, provider, model, runtime, support, message));
    }
}
