using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class ExecutionCapabilityTests
{
    [TestMethod]
    public void EffectiveCapabilitiesIntersectEveryExecutionLevel()
    {
        var provider = Capabilities(CapabilitySupport.Native, new HashSet<ReasoningEffort> { ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High });
        var model = Capabilities(CapabilitySupport.Native, new HashSet<ReasoningEffort> { ReasoningEffort.Medium, ReasoningEffort.High });
        var runtime = Capabilities(CapabilitySupport.Native, new HashSet<ReasoningEffort> { ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High });
        var adapter = Capabilities(CapabilitySupport.Partial, new HashSet<ReasoningEffort> { ReasoningEffort.Medium });

        var effective = EffectiveCapabilityResolver.Intersect(provider, model, runtime, adapter);

        Assert.AreEqual(CapabilitySupport.Partial, effective.Reasoning.Support);
        CollectionAssert.AreEquivalent(new[] { ReasoningEffort.Medium }, effective.Reasoning.SupportedEfforts.ToArray());
        Assert.AreEqual(CapabilitySupport.Native, effective.Streaming.Support);
    }

    [TestMethod]
    public void ValidationReportsProviderModelRuntimeAndEffectiveSupport()
    {
        var profile = Profile() with
        {
            Reasoning = new ModelReasoningOptions { Mode = ReasoningMode.Enabled, Effort = ReasoningEffort.High }
        };
        var capabilities = EffectiveCapabilityResolver.Intersect(
            Capabilities(CapabilitySupport.Unsupported, new HashSet<ReasoningEffort>()) with { Streaming = new() });

        var exception = Assert.ThrowsExactly<ExecutionCompatibilityException>(() => ExecutionCompatibilityValidator.Validate(
            profile,
            new ModelExecutionOptions(Streaming: StreamingMode.Enabled),
            capabilities,
            "ollama",
            "qwen3:8b",
            "microsoft-agent-framework",
            endpointMode: "generate"));

        Assert.IsTrue(exception.Issues.Any(issue => issue.Capability == "reasoning"));
        Assert.IsTrue(exception.Issues.Any(issue => issue.Capability == "streaming"));
        Assert.IsTrue(exception.Issues.Any(issue => issue.Capability == "endpointMode"));
        Assert.IsTrue(exception.Issues.All(issue => issue.Provider == "ollama" && issue.Model == "qwen3:8b"));
    }

    [TestMethod]
    public void OptionsAreMergedByCategoryFromLeastToMostSpecific()
    {
        var resolved = CanonicalOptionResolver.Resolve(
            new CanonicalOptionLayer
            {
                Generation = new ModelGenerationOptions { Temperature = 0.8, TopP = 0.9, MaxOutputTokens = 1000 },
                Streaming = StreamingMode.Disabled
            },
            new CanonicalOptionLayer
            {
                Generation = new ModelGenerationOptions { Temperature = 0.2 },
                Reasoning = new ModelReasoningOptions { Mode = ReasoningMode.Enabled, Effort = ReasoningEffort.Medium }
            },
            new CanonicalOptionLayer
            {
                Generation = new ModelGenerationOptions { MaxOutputTokens = 4096 },
                Streaming = StreamingMode.Enabled
            });

        Assert.AreEqual(0.2, resolved.Generation.Temperature);
        Assert.AreEqual(0.9, resolved.Generation.TopP);
        Assert.AreEqual(4096, resolved.Generation.MaxOutputTokens);
        Assert.AreEqual(ReasoningEffort.Medium, resolved.Reasoning.Effort);
        Assert.AreEqual(StreamingMode.Enabled, resolved.Execution.Streaming);
    }

    private static AgentRuntimeCapabilities Capabilities(CapabilitySupport reasoning, IReadOnlySet<ReasoningEffort> efforts) => new()
    {
        Streaming = new(CapabilitySupport.Native),
        Sessions = new(CapabilitySupport.Native),
        Tools = new(CapabilitySupport.Native),
        StructuredOutput = new(CapabilitySupport.Native),
        Reasoning = new ReasoningCapability { Support = reasoning, SupportedEfforts = efforts }
    };

    private static ModelProfileProperties Profile() => new()
    {
        DisplayName = "Qwen",
        Provider = new ResourceReference("ollama-local"),
        Model = new ModelSelection { Name = "qwen3:8b" }
    };
}
