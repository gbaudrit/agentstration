using Agentstration.Aep.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class InferenceTimeoutTests
{
    [TestMethod]
    public async Task OllamaStreamingBodyTimeoutHasProviderDiagnostic()
    {
        // A cold test process must reach the blocked body read before its inference budget expires.
        await using var extension = new OllamaFactory("00:00:03", TimeSpan.FromSeconds(30), delayBody: true);
        var provider = extension.Services.GetRequiredService<IEnumerable<IAepModelProvider>>().Single();
        await using var updates = provider.ChatStreamingAsync(new("test-model", [new(Agentstration.Aep.Abstractions.AepRole.User,
            [Agentstration.Aep.Abstractions.AepContent.FromText("ping")])]), default).GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(updates.Current.Contents.Any(content => content.Text == "partial"));

        var next = updates.MoveNextAsync().AsTask();
        await extension.BodyReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<AepServerException>(() => next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual("provider_timeout", error.Code);
        Assert.AreEqual(504, error.StatusCode);
        await extension.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, extension.Calls);
    }
}
