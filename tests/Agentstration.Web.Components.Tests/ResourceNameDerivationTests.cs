using Agentstration.Web.Components.Naming;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceNameDerivationTests
{
    [DataRow("Crème brûlée & café", "creme-brulee-cafe")]
    [DataRow("  Repeated--- separators___here  ", "repeated-separators___here")]
    [DataRow("東京", "")]
    [TestMethod]
    public void GenericPolicyNormalizesRepresentativeDisplayNames(string displayName, string expected) =>
        Assert.AreEqual(expected, ResourceNameDerivation.Derive(displayName, ResourceNamePolicy.Generic));

    [TestMethod]
    public void EveryNamedPolicyEnforcesItsMaximumLengthWithoutTrailingSeparator()
    {
        var policies = new[]
        {
            ResourceNamePolicy.Generic, ResourceNamePolicy.Flow, ResourceNamePolicy.Entry, ResourceNamePolicy.Dashboard,
            ResourceNamePolicy.Parameter, ResourceNamePolicy.Trigger, ResourceNamePolicy.SourceRegistry,
            ResourceNamePolicy.Tenant, ResourceNamePolicy.Workspace, ResourceNamePolicy.Pack
        };

        foreach (var policy in policies)
        {
            var value = ResourceNameDerivation.Derive(new string('a', policy.MaximumLength - 1) + " ! suffix", policy);
            Assert.IsLessThanOrEqualTo(policy.MaximumLength, value.Length, policy.Id);
            Assert.IsFalse(value.EndsWith(policy.Separator), policy.Id);
            Assert.IsTrue(value.All(character => policy.IsAllowed(character) || character == policy.Separator), policy.Id);
        }
    }

    [TestMethod]
    public void RestrictivePoliciesUsePortableLowercaseKebabNames()
    {
        foreach (var policy in new[] { ResourceNamePolicy.Trigger, ResourceNamePolicy.SourceRegistry, ResourceNamePolicy.Tenant, ResourceNamePolicy.Workspace, ResourceNamePolicy.Pack })
            Assert.AreEqual("hello-world", ResourceNameDerivation.Derive("Héllo...World", policy), policy.Id);
    }

    [TestMethod]
    public void DistinctPoliciesPreserveOnlyTheirAuthoritativeCharacterSets()
    {
        Assert.AreEqual("alpha.beta_gamma", ResourceNameDerivation.Derive("Alpha.Beta_Gamma", ResourceNamePolicy.Generic));
        Assert.AreEqual("alpha-beta_gamma", ResourceNameDerivation.Derive("Alpha.Beta_Gamma", ResourceNamePolicy.Flow));
        Assert.AreEqual("alpha-beta_gamma", ResourceNameDerivation.Derive("Alpha.Beta_Gamma", ResourceNamePolicy.Entry));
        Assert.AreEqual("alpha-beta_gamma", ResourceNameDerivation.Derive("Alpha.Beta_Gamma", ResourceNamePolicy.Dashboard));
        Assert.AreEqual("alpha.beta-gamma", ResourceNameDerivation.Derive("Alpha.Beta_Gamma", ResourceNamePolicy.Parameter));
    }

    [TestMethod]
    public void ReservedDerivedValueIsLeftEmptyForAuthoritativeValidation()
    {
        var policy = ResourceNamePolicy.Generic with { ReservedValues = new HashSet<string>(["reserved"], StringComparer.Ordinal) };

        Assert.AreEqual(string.Empty, ResourceNameDerivation.Derive("Reserved", policy));
    }
}
