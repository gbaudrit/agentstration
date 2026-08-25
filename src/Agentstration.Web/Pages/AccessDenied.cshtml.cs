using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;

namespace Agentstration.Web.Pages;

[AllowAnonymous]
public sealed class AccessDeniedModel : PageModel
{
    public void OnGet() { }
}
