using System.Globalization;
using System.Text.Json;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Tools;

public sealed class DateTimeMcpTool(TimeProvider timeProvider) : IInternalMcpToolHandler
{
    public InternalMcpToolDefinition Definition { get; } = new(
        AgentstrationInternalTools.DateTimeGet,
        "Get current date and time",
        "Returns the current UTC and host-local date and time with explicit timezone and UTC offset information.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            additionalProperties = false
        }),
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                utc = new { type = "string", format = "date-time" },
                local = new { type = "string", format = "date-time" },
                timeZone = new { type = "string" },
                utcOffset = new { type = "string" }
            },
            required = new[] { "utc", "local", "timeZone", "utcOffset" },
            additionalProperties = false
        }),
        InitialCategory: new(
            "core-tools",
            "Outils de base",
            "Outils génériques utiles à de nombreux agents."));

    public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.Arguments.ValueKind != JsonValueKind.Object || invocation.Arguments.EnumerateObject().Any())
            throw new ToolDefinitionInvocationException("datetime_arguments_invalid", "The datetime Tool does not accept arguments.");

        var utc = timeProvider.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local);
        return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
        {
            utc = utc.ToString("O", CultureInfo.InvariantCulture),
            local = local.ToString("O", CultureInfo.InvariantCulture),
            timeZone = TimeZoneInfo.Local.Id,
            utcOffset = local.Offset.ToString("c", CultureInfo.InvariantCulture)
        }));
    }
}
