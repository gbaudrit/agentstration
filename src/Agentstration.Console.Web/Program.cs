using Agentstration.Console.Web;
using Agentstration.Console.Web.Components;
using Agentstration.Console.Web.Configuration;
using Agentstration.Http.Diagnostics;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Localization;
using Agentstration.Web.Console;

var builder = WebApplication.CreateBuilder(args);
builder.AddAgentstrationHttpDiagnostics();
builder.Services.AddAgentstrationConsolePresentation(builder.Configuration);
builder.Services.AddAgentstrationConsoleHost(builder.Configuration);
builder.Services.AddAgentstrationConsoleObservability(builder.Logging, builder.Configuration);

var app = builder.Build();
app.UseAgentstrationHttpDiagnostics();
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
app.MapPost("/bff/identity/context/selection", async (
    SelectConsoleContextRequest request,
    IIdentityAdministrationApiClient identity,
    CancellationToken cancellationToken) =>
{
    await identity.SelectConsoleContextAsync(request.ScopeRef, cancellationToken);
    return Results.Ok();
}).RequireAuthorization().DisableAntiforgery();
app.MapStaticAssets();
app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(ConsoleRouteAssembly).Assembly)
    .RequireAuthorization();
await app.RunAsync();

public partial class Program;
