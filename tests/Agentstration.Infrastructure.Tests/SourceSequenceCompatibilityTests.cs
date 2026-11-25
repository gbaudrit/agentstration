using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class SourceSequenceCompatibilityTests
{
    [TestMethod]
    [DataRow("{\"type\":\"integer\"}", "{\"type\":\"number\"}", SequenceCompatibility.Verified)]
    [DataRow("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"],\"additionalProperties\":false}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"number\"}},\"required\":[\"id\"],\"additionalProperties\":false}", SequenceCompatibility.Incompatible)]
    [DataRow("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "{\"type\":\"object\"}", SequenceCompatibility.Incompatible)]
    [DataRow("{\"type\":\"object\",\"properties\":{\"id\":{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"number\"}]}}}", "{\"type\":\"object\"}", SequenceCompatibility.Verified)]
    [DataRow("{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"number\"}]}", "{\"type\":\"string\"}", SequenceCompatibility.Undetermined)]
    [DataRow("{\"type\":\"string\"}", "{}", SequenceCompatibility.Verified)]
    public void AuthoringUsesAdmissionProofsAndKeepsUnresolvedConstraintsUnknown(string producer, string consumer, SequenceCompatibility expected)
    {
        Assert.AreEqual(expected, SourceSequenceService.Compatibility(JsonSerializer.Deserialize<JsonElement>(producer), JsonSerializer.Deserialize<JsonElement>(consumer)));
    }
}
