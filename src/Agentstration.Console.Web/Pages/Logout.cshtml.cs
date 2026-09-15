using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Agentstration.Console.Web.Pages;

[Authorize]
public sealed class LogoutModel : PageModel
{
    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(ConsoleAuthenticationDefaults.Scheme);
        return RedirectToPage("/Login");
    }
}
