using Agentstration.Console.Web;
using Agentstration.Console.Web.Components;
using Agentstration.Console.Web.Configuration;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Localization;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAgentstrationConsoleHost(builder.Configuration);
builder.Services.AddAgentstrationLocalization(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication(ConsoleAuthenticationDefaults.Scheme)
    .AddCookie(ConsoleAuthenticationDefaults.Scheme, options =>
    {
        options.Cookie.Name = ConsoleAuthenticationDefaults.Cookie;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/login";
        options.ReturnUrlParameter = "returnUrl";
    });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var otlpEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Agentstration.Console.Web"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (otlpEnabled) tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (otlpEnabled) metrics.AddOtlpExporter();
    });

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapAgentstrationCultureEndpoint().AllowAnonymous();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(ConsoleRouteAssembly).Assembly);
await app.RunAsync();

public partial class Program;
