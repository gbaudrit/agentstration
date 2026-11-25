using Agentstration.Tools.Mcp;

namespace Agentstration.Web.Hosting;

/// <summary>Bounds discovery sharing to a finite REST operation; streaming MCP calls own their scope.</summary>
public sealed class AepDiscoveryOperationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AepMcpTransport transport)
    {
        if ((!context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/bff"))
            || context.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        using var discovery = transport.BeginDiscoveryOperation(context.RequestAborted);
        await next(context);
    }
}
