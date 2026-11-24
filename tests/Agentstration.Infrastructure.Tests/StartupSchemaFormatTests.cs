using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Schemas;
using Agentstration.Schemas;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class StartupSchemaFormatTests
{
    [TestMethod]
    public async Task BuiltinPublicationDoesNotContactOptionalFormatProvidersBeforeContributionsAreReady()
    {
        var startup = new BootstrapStartupState();
        var remote = new RemoteSource();
        var source = new StartupSchemaFormatHandlerSource(remote, startup);
        var registry = new SchemaFormatRegistry([new BuiltinJsonSchemaFormatHandler()], [source]);
        startup.Begin();
        foreach (var phase in new[] { "topology", "workers", "builtins", "finalization" })
        {
            startup.MoveTo(phase);
            var format = await registry.ResolveAsync(SchemaFormatIds.JsonSchema, SchemaFormatCapabilities.ValidateDefinition);
            Assert.AreEqual(BuiltinJsonSchemaFormatHandler.ProviderId, format.Descriptor.Provider.Id);
        }
        Assert.AreEqual(0, remote.Calls);
        startup.MoveTo("resources");
        _ = await registry.ListAsync();
        Assert.AreEqual(1, remote.Calls);
        startup.Complete();
        _ = await registry.ListAsync();
        Assert.AreEqual(2, remote.Calls);
    }

    private sealed class RemoteSource : ISchemaFormatHandlerSource
    {
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyList<ISchemaFormatHandler>> ListAsync(CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult<IReadOnlyList<ISchemaFormatHandler>>([]); }
    }
}
