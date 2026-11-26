using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;

namespace Agentstration.Aep.Tests;

[TestClass]
public sealed class OperationalConfigurationTests
{
    [TestMethod]
    public void DescriptorPinsMetadataAndKeepsScopeAndActivationIndependent()
    {
        var descriptor = AepOperationalConfigurationDescriptor.Create("fixture/installation", "1.0.0",
            [new("Domains", JsonSerializer.SerializeToElement(new { type = "array", maxItems = 128, items = new { type = "string", maxLength = 253 } }),
                true, AepValueProtection.Standard, [AepConfigurationValueSource.Literal, AepConfigurationValueSource.Parameter],
                AepConfigurationScope.ConsumerContext, AepConfigurationApplyMode.NewBinding, "network")]);
        Assert.IsTrue(descriptor.IsValid());
        var roundTrip = JsonSerializer.Deserialize<AepOperationalConfigurationDescriptor>(JsonSerializer.Serialize(descriptor, AepProtocol.JsonOptions), AepProtocol.JsonOptions)!;
        Assert.IsTrue(roundTrip.IsValid());
        Assert.AreEqual(AepConfigurationScope.ConsumerContext, roundTrip.Options[0].Scope);
        Assert.AreEqual(AepConfigurationApplyMode.NewBinding, roundTrip.Options[0].ApplyMode);
        Assert.AreEqual("network", roundTrip.Options[0].AtomicGroup);
        Assert.IsFalse((descriptor with { Options = [descriptor.Options[0] with { ApplyMode = AepConfigurationApplyMode.Immediate }] }).IsValid());
        Assert.IsFalse((descriptor with { Options = [descriptor.Options[0], descriptor.Options[0]] }).IsValid());
    }

    [TestMethod]
    public void SecuredDescriptorsCannotPermitPersistentPlaintextSources()
    {
        var option = new AepOperationalOption("Credential", JsonSerializer.SerializeToElement(new { type = "string" }),
            true, AepValueProtection.Secured, [AepConfigurationValueSource.Secret], AepConfigurationScope.Process,
            AepConfigurationApplyMode.RestartRequired);
        Assert.IsTrue(AepOperationalConfigurationDescriptor.Create("fixture/secured", "1.0.0", [option]).IsValid());
        foreach (var source in new[] { AepConfigurationValueSource.Literal, AepConfigurationValueSource.Parameter })
            Assert.IsFalse(AepOperationalConfigurationDescriptor.Create("fixture/secured", "1.0.0", [option with { AllowedSources = [source] }]).IsValid());
    }

    [TestMethod]
    public void StructuredValueValidationEnforcesListAndStringBounds()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "array", maxItems = 2, items = new { type = "string", minLength = 1, maxLength = 4 } });
        Assert.IsEmpty(AepOptionSchemaValidator.Validate(JsonSerializer.Deserialize<JsonElement>("[\"a\",\"abcd\"]"), schema));
        Assert.IsNotEmpty(AepOptionSchemaValidator.Validate(JsonSerializer.Deserialize<JsonElement>("[\"\"]"), schema));
        Assert.IsNotEmpty(AepOptionSchemaValidator.Validate(JsonSerializer.Deserialize<JsonElement>("[\"longer\"]"), schema));
        Assert.IsNotEmpty(AepOptionSchemaValidator.Validate(JsonSerializer.Deserialize<JsonElement>("[\"a\",\"b\",\"c\"]"), schema));
        Assert.IsNotEmpty(AepOptionSchemaValidator.Validate(JsonSerializer.SerializeToElement("a"), schema));
    }
    [TestMethod]
    public async Task ClientRejectsAValidButDifferentContractInActivationAcknowledgment()
    {
        var descriptor = AepOperationalConfigurationDescriptor.Create("fixture/installation", "1.0.0",
            [new("Value", JsonSerializer.SerializeToElement(new { type = "string" }), true,
                AepValueProtection.Standard, [AepConfigurationValueSource.Literal],
                AepConfigurationScope.Process, AepConfigurationApplyMode.Immediate)]);
        var state = new AepOperationalConfigurationState(descriptor with { Version = "2.0.0" }, "revision", true,
            new Dictionary<string, JsonElement>());
        using var http = new HttpClient(new AcknowledgmentHandler(state)) { BaseAddress = new Uri("http://127.0.0.1:12345") };
        var request = new AepOperationalConfigurationRequest(descriptor.Id, descriptor.Version, descriptor.SchemaDigest,
            Guid.NewGuid(), 1, "revision", null, [AepBoundValue.Inline("Value", JsonSerializer.SerializeToElement("candidate"))]);
        var exception = await Assert.ThrowsExactlyAsync<AepProtocolException>(() => new AepClient(http).ApplyOperationalConfigurationAsync(request));
        Assert.AreEqual("configuration_acknowledgment_invalid", exception.Code);
    }

    private sealed class AcknowledgmentHandler(AepOperationalConfigurationState state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(state, options: AepProtocol.JsonOptions) });
    }
}
