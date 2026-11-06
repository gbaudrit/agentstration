using System.Text.Json;
using Agentstration.Infrastructure.Tools;
using Agentstration.Tools;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DateTimeToolTests
{
    [TestMethod]
    public async Task NativeDateTimeToolReturnsExplicitUtcLocalTimezoneAndOffset()
    {
        var expected = new DateTimeOffset(2026, 9, 24, 10, 11, 12, TimeSpan.Zero);
        var tool = new DateTimeMcpTool(new FixedTimeProvider(expected));
        var result = await tool.ExecuteAsync(new(
            Guid.NewGuid(),
            new(Guid.NewGuid()),
            Guid.NewGuid(),
            "date-time-call",
            null,
            JsonSerializer.SerializeToElement(new { }),
            ToolDefinitionCallerKind.Agent),
            default);

        Assert.IsNotNull(result);
        Assert.AreEqual(expected, result.Value.GetProperty("utc").GetDateTimeOffset());
        Assert.AreEqual(TimeZoneInfo.Local.Id, result.Value.GetProperty("timeZone").GetString());
        var local = result.Value.GetProperty("local").GetDateTimeOffset();
        Assert.AreEqual(TimeZoneInfo.ConvertTime(expected, TimeZoneInfo.Local), local);
        Assert.AreEqual(local.Offset.ToString("c"), result.Value.GetProperty("utcOffset").GetString());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
