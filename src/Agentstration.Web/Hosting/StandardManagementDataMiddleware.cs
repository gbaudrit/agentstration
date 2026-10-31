using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Profiles;

namespace Agentstration.Web.Hosting;

public sealed class StandardManagementDataMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        StandardRuntimeProfileSeeder seeder,
        IRequestContextScopeFactory requestScopes)
    {
        using (requestScopes.PushSystem())
            await seeder.EnsureAsync(ResourceScopeRef.Instance, context.RequestAborted);
        await next(context);
    }
}
