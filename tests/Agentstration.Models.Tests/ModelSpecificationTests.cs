using Agentstration.Models;
using Agentstration.ResourceManagement;

namespace Agentstration.Models.Tests;

[TestClass]
public sealed class ModelSpecificationTests
{
    [TestMethod]
    public void SpecificationRoundTripsThroughJsonAndYaml()
    {
        var expected = Specification();

        var json = ResourceManifestSerializer.ToJson(expected);
        var yaml = ResourceManifestSerializer.ToYaml(expected);
        var fromJson = ResourceManifestSerializer.FromJsonStrict<ModelSpecification>(json);
        var fromYaml = ResourceManifestSerializer.FromYaml<ModelSpecification>(yaml);

        AssertSpecification(expected, fromJson);
        AssertSpecification(expected, fromYaml);
        StringAssert.Contains(json, "\"jsonSchema\"");
        StringAssert.Contains(yaml, "supportsStrict: true");
    }

    [TestMethod]
    public void MissingObservationRemainsUnknownAndDistinctFromUnsupported()
    {
        var effective = EffectiveModelSpecificationResolver.Resolve(new ModelSpecification
        {
            Features = new ModelFeatureSpecifications
            {
                Tools = new(),
                StructuredOutput = new() { Support = ModelFeatureSupport.Unsupported }
            }
        });

        Assert.AreEqual(ModelFeatureSupport.Unknown, effective.Features.Tools!.Support);
        Assert.AreEqual(ModelFeatureSupport.Unsupported, effective.Features.StructuredOutput!.Support);
    }

    [TestMethod]
    public void OverrideCompletesUnknownSupportAndAddsTypedDetails()
    {
        var effective = EffectiveModelSpecificationResolver.Resolve(
            new ModelSpecification
            {
                Input = [ModelContentType.Text],
                Features = new ModelFeatureSpecifications { Tools = new() }
            },
            new ModelSpecificationOverride
            {
                Input = new() { Add = [ModelContentType.Image] },
                Features = new ModelFeatureOverrides
                {
                    Tools = new()
                    {
                        Support = ModelFeatureSupport.Native,
                        Modes = new()
                        {
                            Add = new Dictionary<ModelToolMode, ModelToolModeSpecification>
                            {
                                [ModelToolMode.Function] = new()
                            }
                        }
                    }
                }
            });

        CollectionAssert.AreEqual(
            new[] { ModelContentType.Text, ModelContentType.Image },
            effective.Input!.ToArray());
        Assert.AreEqual(ModelFeatureSupport.Native, effective.Features.Tools!.Support);
        Assert.IsTrue(effective.Features.Tools.Modes.ContainsKey(ModelToolMode.Function));
    }

    [TestMethod]
    public void OverrideCannotElevateExplicitUnsupportedSupport()
    {
        var effective = EffectiveModelSpecificationResolver.Resolve(
            new ModelSpecification
            {
                Features = new ModelFeatureSpecifications
                {
                    Tools = new() { Support = ModelFeatureSupport.Unsupported }
                }
            },
            new ModelSpecificationOverride
            {
                Features = new ModelFeatureOverrides
                {
                    Tools = new()
                    {
                        Support = ModelFeatureSupport.Native,
                        Modes = new()
                        {
                            Add = new Dictionary<ModelToolMode, ModelToolModeSpecification>
                            {
                                [ModelToolMode.Function] = new()
                            }
                        }
                    }
                }
            });

        Assert.AreEqual(ModelFeatureSupport.Unsupported, effective.Features.Tools!.Support);
        Assert.AreEqual(0, effective.Features.Tools.Modes.Count);
    }

    [TestMethod]
    public void CollectionRemovalWinsAndDoesNotMutateObservation()
    {
        var observed = new ModelSpecification
        {
            Input = [ModelContentType.Text, ModelContentType.Image],
            Features = new ModelFeatureSpecifications
            {
                Tools = new()
                {
                    Support = ModelFeatureSupport.Native,
                    Modes = new Dictionary<ModelToolMode, ModelToolModeSpecification>
                    {
                        [ModelToolMode.Function] = new()
                    }
                }
            }
        };
        var effective = EffectiveModelSpecificationResolver.Resolve(observed, new ModelSpecificationOverride
        {
            Input = new()
            {
                Add = [ModelContentType.Audio],
                Remove = [ModelContentType.Image, ModelContentType.Audio]
            },
            Features = new ModelFeatureOverrides
            {
                Tools = new()
                {
                    Modes = new()
                    {
                        Add = new Dictionary<ModelToolMode, ModelToolModeSpecification>
                        {
                            [ModelToolMode.Parallel] = new()
                        },
                        Remove = [ModelToolMode.Function]
                    }
                }
            }
        });

        CollectionAssert.AreEqual(new[] { ModelContentType.Text }, effective.Input!.ToArray());
        CollectionAssert.AreEqual(
            new[] { ModelToolMode.Parallel },
            effective.Features.Tools!.Modes.Keys.ToArray());
        CollectionAssert.AreEqual(
            new[] { ModelContentType.Text, ModelContentType.Image },
            observed.Input!.ToArray());
        Assert.IsTrue(observed.Features.Tools!.Modes.ContainsKey(ModelToolMode.Function));
    }

    [TestMethod]
    public void LimitsAreSuppliedWhenUnknownAndRestrictedWhenKnown()
    {
        var supplied = EffectiveModelSpecificationResolver.Resolve(
            new ModelSpecification(),
            new ModelSpecificationOverride
            {
                Limits = new ModelLimitOverrides { ContextTokens = 128_000 }
            });
        var restricted = EffectiveModelSpecificationResolver.Resolve(
            new ModelSpecification
            {
                Limits = new ModelLimits { ContextTokens = 200_000, MaxOutputTokens = 64_000 }
            },
            new ModelSpecificationOverride
            {
                Limits = new ModelLimitOverrides { ContextTokens = 128_000, MaxOutputTokens = 128_000 }
            });

        Assert.AreEqual(128_000, supplied.Limits.ContextTokens);
        Assert.AreEqual(128_000, restricted.Limits.ContextTokens);
        Assert.AreEqual(64_000, restricted.Limits.MaxOutputTokens);
    }

    [TestMethod]
    public void DuplicateOverrideValuesAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => EffectiveModelSpecificationResolver.Resolve(
            new ModelSpecification(),
            new ModelSpecificationOverride
            {
                Input = new() { Add = [ModelContentType.Text, ModelContentType.Text] }
            }));
    }

    private static ModelSpecification Specification() => new()
    {
        Input = [ModelContentType.Text, ModelContentType.Image],
        Output = [ModelContentType.Text],
        Features = new ModelFeatureSpecifications
        {
            Streaming = new() { Support = ModelFeatureSupport.Native },
            Tools = new()
            {
                Support = ModelFeatureSupport.Native,
                Modes = new Dictionary<ModelToolMode, ModelToolModeSpecification>
                {
                    [ModelToolMode.Function] = new(),
                    [ModelToolMode.Parallel] = new()
                }
            },
            StructuredOutput = new()
            {
                Support = ModelFeatureSupport.Native,
                Formats = new Dictionary<ModelStructuredOutputFormat, ModelStructuredOutputFormatSpecification>
                {
                    [ModelStructuredOutputFormat.JsonObject] = new(),
                    [ModelStructuredOutputFormat.JsonSchema] = new() { SupportsStrict = true }
                }
            },
            Reasoning = new()
            {
                Support = ModelFeatureSupport.Native,
                Efforts = new Dictionary<ReasoningEffort, ModelReasoningEffortSpecification>
                {
                    [ReasoningEffort.Low] = new(),
                    [ReasoningEffort.Medium] = new(),
                    [ReasoningEffort.High] = new()
                }
            }
        },
        Limits = new ModelLimits { ContextTokens = 200_000, MaxOutputTokens = 64_000 }
    };

    private static void AssertSpecification(ModelSpecification expected, ModelSpecification actual)
    {
        CollectionAssert.AreEqual(expected.Input!.ToArray(), actual.Input!.ToArray());
        CollectionAssert.AreEqual(expected.Output!.ToArray(), actual.Output!.ToArray());
        Assert.AreEqual(expected.Features.Streaming, actual.Features.Streaming);
        Assert.AreEqual(expected.Features.Tools!.Support, actual.Features.Tools!.Support);
        CollectionAssert.AreEquivalent(
            expected.Features.Tools.Modes.Keys.ToArray(),
            actual.Features.Tools.Modes.Keys.ToArray());
        Assert.AreEqual(
            expected.Features.StructuredOutput!.Formats[ModelStructuredOutputFormat.JsonSchema],
            actual.Features.StructuredOutput!.Formats[ModelStructuredOutputFormat.JsonSchema]);
        CollectionAssert.AreEquivalent(
            expected.Features.Reasoning!.Efforts.Keys.ToArray(),
            actual.Features.Reasoning!.Efforts.Keys.ToArray());
        Assert.AreEqual(expected.Limits, actual.Limits);
    }
}
