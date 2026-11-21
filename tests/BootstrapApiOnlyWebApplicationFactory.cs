using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Management.Tests;

internal sealed class BootstrapApiOnlyWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Agentstration:Testing:ApiOnly", "true");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }
}
