using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Asc.Abstractions;

namespace Agentstration.Asc.Tests;

[TestClass]
public sealed class AscIdentityTests
{
    [TestMethod]
    public void StandaloneContractAndAepExposureAgree()
    {
        var mapping = new AepStorageAscMapping(AscContract.Profile, "content", "asc", "fixture", "1.0")
        {
            Contract = AscContract.Identity,
            Revision = AscContract.Version,
            McpProtocolVersion = AscContract.McpProtocolVersion
        };
        var wire = JsonSerializer.SerializeToElement(mapping, AepProtocol.JsonOptions);
        Assert.AreEqual(AepStorageProtocol.Contract, wire.GetProperty("contract").GetString());
        Assert.AreEqual(AepStorageProtocol.ContractRevision, wire.GetProperty("revision").GetString());
        Assert.AreEqual(AepStorageProtocol.McpProtocolVersion, wire.GetProperty("mcpProtocolVersion").GetString());
    }
}
