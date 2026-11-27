using Agentstration.Aep.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class InferenceTimeoutTests
{
    [TestMethod]
    public async Task OllamaStreamingBodyTimeoutHasProviderDiagnostic()
    {
        await using var extension = new OllamaFactory("00:00:00.100", TimeSpan.FromSeconds(5), delayBody: true);
        var provider = extension.Services.GetRequiredService<IEnumerable<IAepModelProvider>>().Single();
        var error = await Assert.ThrowsAsync<AepServerException>(async () =>
        {
            await foreach (var _ in provider.ChatStreamingAsync(new("test-model", [new(Agentstration.Aep.Abstractions.AepRole.User,
                [Agentstration.Aep.Abstractions.AepContent.FromText("ping")])]), default)) { }
        });
        Assert.AreEqual("provider_timeout", error.Code);
        Assert.AreEqual(504, error.StatusCode);
        await extension.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
