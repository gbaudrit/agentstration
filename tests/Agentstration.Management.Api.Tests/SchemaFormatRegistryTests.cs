using System.Text.Json;
using Agentstration.Schemas;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SchemaFormatRegistryTests
{
    [TestMethod]
    public void BuiltinProviderResolvesWithoutAep()
    {
        var registry = new SchemaFormatRegistry([new BuiltinJsonSchemaFormatHandler()]);

        var handler = registry.Resolve(SchemaFormatIds.JsonSchema, SchemaFormatCapabilities.ValidateDefinition);

        Assert.AreEqual(BuiltinJsonSchemaFormatHandler.ProviderId, handler.Descriptor.Provider.Id);
        Assert.IsTrue(handler.Descriptor.IsDefault);
    }

    [TestMethod]
    public void DuplicateProvidersRequireAnExplicitDefaultOrPin()
    {
        var first = new StubHandler("extension/one", false);
        var second = new StubHandler("extension/two", false);
        var registry = new SchemaFormatRegistry([first, second]);

        var ambiguous = Assert.ThrowsExactly<SchemaFormatResolutionException>(() => registry.Resolve(
            "custom", SchemaFormatCapabilities.ValidateDefinition));
        Assert.AreEqual("schema_format_provider_ambiguous", ambiguous.Code);

        var pinned = registry.Resolve("custom", SchemaFormatCapabilities.ValidateDefinition,
            first.Descriptor.Provider);
        Assert.AreSame(first, pinned);
    }

    [TestMethod]
    public void MissingCapabilityAndUnavailableProviderAreExplicit()
    {
        var missing = new SchemaFormatRegistry([new StubHandler("extension/one", false)]);
        var capability = Assert.ThrowsExactly<SchemaFormatResolutionException>(() => missing.Resolve(
            "custom", SchemaFormatCapabilities.ValidateInstance));
        Assert.AreEqual("schema_format_capability_unavailable", capability.Code);

        var unavailable = new SchemaFormatRegistry([new StubHandler("extension/one", true)]);
        var provider = Assert.ThrowsExactly<SchemaFormatResolutionException>(() => unavailable.Resolve(
            "custom", SchemaFormatCapabilities.ValidateDefinition));
        Assert.AreEqual("schema_format_provider_unavailable", provider.Code);
    }

    private sealed class StubHandler(string provider, bool unavailable) : ISchemaFormatHandler
    {
        public SchemaFormatDescriptor Descriptor { get; } = new(
            "custom", "Custom", new(provider, "1.0"), [], SchemaFormatCapabilities.ValidateDefinition,
            IsDefault: false, IsAvailable: !unavailable, UnavailableReason: unavailable ? "Provider offline." : null);

        public ValueTask<SchemaFormatValidationResult> ValidateDefinitionAsync(JsonElement definition,
            string? dialect, CancellationToken cancellationToken) => ValueTask.FromResult(SchemaFormatValidationResult.Valid);

        public ValueTask<SchemaFormatValidationResult> ValidateInstanceAsync(JsonElement definition,
            JsonElement instance, string? dialect, CancellationToken cancellationToken) =>
            ValueTask.FromResult(SchemaFormatValidationResult.Valid);
    }
}
