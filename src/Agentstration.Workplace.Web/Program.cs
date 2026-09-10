using Agentstration.Web.Components.Localization;
using Agentstration.Workplace.Web;
using Agentstration.Workplace.Web.Components;

var builder = WebApplication.CreateBuilder(args);
var apiValue = builder.Configuration["Agentstration:ApiBaseUrl"] ?? throw new InvalidOperationException("Agentstration:ApiBaseUrl is required.");
if (!Uri.TryCreate(apiValue, UriKind.Absolute, out var apiUrl) || apiUrl.Scheme is not ("http" or "https")) throw new InvalidOperationException("Agentstration:ApiBaseUrl must be an absolute HTTP(S) URL.");
var hubValue = builder.Configuration["Agentstration:WorkplaceHubUrl"];
hubValue = string.IsNullOrWhiteSpace(hubValue) ? new Uri(apiUrl, "hubs/workplace").ToString() : hubValue;
if (!Uri.TryCreate(hubValue, UriKind.Absolute, out var hubUrl) || hubUrl.Scheme is not ("http" or "https")) throw new InvalidOperationException("Agentstration:WorkplaceHubUrl must be an absolute HTTP(S) URL.");
builder.Services.AddAgentstrationWorkplaceHost(builder.Configuration, apiUrl, hubUrl);
builder.AddAgentstrationWorkplaceObservability();
var app = builder.Build(); app.UseExceptionHandler(); app.UseStatusCodePages(); app.UseRequestLocalization(); app.UseAntiforgery(); app.MapHealthChecks("/health"); app.MapAgentstrationCultureEndpoint(); app.MapStaticAssets(); app.MapRazorComponents<App>().AddInteractiveServerRenderMode(); await app.RunAsync();
public partial class Program;
