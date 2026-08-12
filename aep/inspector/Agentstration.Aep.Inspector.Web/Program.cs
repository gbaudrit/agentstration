using Agentstration.Aep.Inspector;
using Agentstration.Aep.Inspector.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<InspectorSession>();
var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.RunAsync();

public partial class Program;
