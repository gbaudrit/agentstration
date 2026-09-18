using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Sources.Contracts;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class ExtensionSecretBindingTests
{
    private static readonly AepSecretRequirement[] Requirements =
    [
        new("credential", true),
        new("proxy-auth", false)
    ];

    [TestMethod]
    public void ConsumersCanBindTheSameRequirementToDifferentScopedSecrets()
    {
        var firstScope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var secondScope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var first = new SecretBinding("credential", Reference("company-a", firstScope));
        var second = new SecretBinding("credential", Reference("company-b", secondScope));

        Assert.IsEmpty(ExtensionSecretBindingValidator.Validate([first], Requirements, requireAll: true));
        Assert.IsEmpty(ExtensionSecretBindingValidator.Validate([second], Requirements, requireAll: true));

        var profile = new ModelProfileProperties
        {
            DisplayName = "Consumer A",
            Provider = new ResourceReference("provider"),
            Model = new ModelSelection { Name = "model" },
            SecretBindings = [first]
        };
        var source = new SourceBindingSelection
        {
            Name = "provider",
            TargetKind = SourceResourceKinds.SourceProvider,
            Target = new ResourceReference("source-provider"),
            SecretBindings = [second]
        };
        var savedProfile = JsonSerializer.Deserialize<ModelProfileProperties>(JsonSerializer.Serialize(profile));
        var savedSource = JsonSerializer.Deserialize<SourceBindingSelection>(JsonSerializer.Serialize(source));

        Assert.IsNotNull(savedProfile);
        Assert.IsNotNull(savedSource);
        Assert.AreEqual(firstScope, savedProfile.SecretBindings.Single().Secret.ScopeRef);
        Assert.AreEqual(secondScope, savedSource.SecretBindings.Single().Secret.ScopeRef);
        Assert.AreEqual("company-a", savedProfile.SecretBindings.Single().Secret.Address.Name);
        Assert.AreEqual("company-b", savedSource.SecretBindings.Single().Secret.Address.Name);
    }

    [TestMethod]
    public void UnknownDuplicateUnscopedAndMissingRequiredBindingsFailClosed()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var valid = new SecretBinding("credential", Reference("company-a", scope));
        var issues = ExtensionSecretBindingValidator.Validate(
            [valid, valid, new("other", Reference("company-b", scope)), new("proxy-auth", new(new(ResourceNamespace.Default, "Secret", "proxy")))],
            Requirements,
            requireAll: true);

        Assert.IsTrue(issues.Any(value => value.Code == "secret_binding_duplicate"));
        Assert.IsTrue(issues.Any(value => value.Code == "secret_binding_unknown"));
        Assert.IsTrue(issues.Any(value => value.Code == "secret_binding_reference_invalid"));
        Assert.IsTrue(ExtensionSecretBindingValidator.Validate([], Requirements, requireAll: true)
            .Any(value => value.Code == "secret_binding_required"));
        Assert.IsEmpty(ExtensionSecretBindingValidator.Validate([], Requirements, requireAll: false));
    }

    private static SecretReference Reference(string name, ResourceScopeRef scope) =>
        new(new(ResourceNamespace.Default, "Secret", name), scope);
}
