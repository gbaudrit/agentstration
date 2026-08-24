using Agentstration.Management.Core;

namespace Agentstration.Web.Hosting;

public sealed class StandardManagementDataMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        StandardRuntimeProfileSeeder seeder)
    {
        await seeder.EnsureAsync(context.RequestAborted);
        await next(context);
    }
}
