using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Agentstration.Web.Pages;

[AllowAnonymous]
public sealed class AccessDeniedModel : PageModel
{
    public void OnGet() { }
}
