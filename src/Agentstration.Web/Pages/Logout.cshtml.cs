using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Agentstration.Web.Pages;

[Authorize(Policy = AgentstrationPolicies.Authenticated)]
public sealed class LogoutModel(SignInManager<LocalIdentityUser> signIn) : PageModel
{
    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync();
        return RedirectToPage("/Login");
    }
}
