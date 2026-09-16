using Agentstration.Console.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Agentstration.Console.Web.Pages;

[Authorize]
public sealed class LogoutModel(BffDelegationTokenCache cache) : PageModel
{
    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (User.FindFirst(BffSessionClaims.SessionId)?.Value is { } sessionId)
            cache.RevokeSession(sessionId);
        await HttpContext.SignOutAsync(ConsoleAuthenticationDefaults.Scheme);
        return RedirectToPage("/Login");
    }
}
