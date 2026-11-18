namespace Agentstration.Extensions.OpenDal;

public static class OpenDalProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 100_000);
        builder.Services.AddOpenDalStorage(builder.Configuration);
        var app = builder.Build();
        app.MapOpenDalStorage();
        await app.RunAsync();
    }
}
