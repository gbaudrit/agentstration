using System.ComponentModel.DataAnnotations;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Configuration;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel(
    SignInManager<LocalIdentityUser> signIn,
    LocalBootstrapCoordinator bootstrap,
    IOptions<AgentstrationWebOptions> options,
    IStringLocalizer<AuthStrings> localizer) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public bool LocalLoginAvailable => AuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode);
    public bool ExternalLoginAvailable => AuthenticationOptions.SupportsExternalLogin(options.Value.Authentication.Mode);
    public string ExternalLoginUrl => $"/api/auth/oidc/login?returnUrl={Uri.EscapeDataString(AuthenticationReturnUrls.Normalize(ReturnUrl))}";

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = AuthenticationReturnUrls.Normalize(ReturnUrl);
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(ReturnUrl);
        if (!LocalLoginAvailable && !ExternalLoginAvailable) return NotFound();
        if (LocalLoginAvailable && !await bootstrap.IsInitializedAsync(cancellationToken))
            return RedirectToPage("/Bootstrap", new { returnUrl = ReturnUrl });
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = AuthenticationReturnUrls.Normalize(ReturnUrl);
        if (!LocalLoginAvailable) return NotFound();
        if (!await bootstrap.IsInitializedAsync(cancellationToken))
            return RedirectToPage("/Bootstrap", new { returnUrl = ReturnUrl });
        if (!ModelState.IsValid) return Page();

        var result = await signIn.PasswordSignInAsync(
            Input.UserName.Trim(), Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(ReturnUrl);

        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? localizer["AccountLocked"].Value
            : localizer["InvalidCredentials"].Value);
        return Page();
    }

    public sealed class LoginInput
    {
        [Required, StringLength(64, MinimumLength = 3), Display(Name = "Username")]
        public string UserName { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Keep me signed in")]
        public bool RememberMe { get; set; }
    }
}
