using System.ComponentModel.DataAnnotations;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Configuration;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Pages;

[AllowAnonymous]
public sealed class BootstrapModel(
    LocalBootstrapCoordinator bootstrap,
    UserManager<LocalIdentityUser> users,
    SignInManager<LocalIdentityUser> signIn,
    IOptions<AgentstrationWebOptions> options) : PageModel
{
    [BindProperty]
    public BootstrapInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = AuthenticationReturnUrls.Normalize(ReturnUrl);
        if (!AuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode)) return NotFound();
        if (await bootstrap.IsInitializedAsync(cancellationToken))
            return RedirectToPage("/Login", new { returnUrl = ReturnUrl });
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = AuthenticationReturnUrls.Normalize(ReturnUrl);
        if (!AuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode)) return NotFound();
        if (await bootstrap.IsInitializedAsync(cancellationToken))
            return RedirectToPage("/Login", new { returnUrl = ReturnUrl });
        if (!ModelState.IsValid) return Page();

        var result = await bootstrap.BootstrapAsync(
            new LocalBootstrapRequest(Input.UserName, Input.Password, Input.DisplayName, Input.Email),
            cancellationToken);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        var account = await users.FindByNameAsync(Input.UserName.Trim());
        if (account is null) throw new InvalidOperationException("The bootstrapped local account could not be loaded.");
        await signIn.SignInAsync(account, isPersistent: false);
        return LocalRedirect(ReturnUrl);
    }

    public sealed class BootstrapInput
    {
        [Required, StringLength(120, MinimumLength = 2), Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [Required, StringLength(64, MinimumLength = 3), Display(Name = "Username")]
        public string UserName { get; set; } = string.Empty;

        [EmailAddress, Display(Name = "Email (optional)")]
        public string? Email { get; set; }

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Compare(nameof(Password)), Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
