using System.ComponentModel.DataAnnotations;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Pages.Account;

[Authorize(Policy = AgentstrationPolicies.Authenticated)]
public sealed class SecurityModel(LocalAccountSecurityService security, IStringLocalizer<AuthStrings> localizer) : PageModel
{
    [BindProperty]
    public ChangePasswordInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    public LocalAccountSecurityView? Account { get; private set; }
    public string? StatusMessage => Status switch
    {
        "password-changed" => localizer["PasswordChangedStatus"].Value,
        "sessions-signed-out" => localizer["SessionsSignedOutStatus"].Value,
        _ => null
    };

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Account = await LoadAccountAsync(cancellationToken);

    public async Task<IActionResult> OnPostChangePasswordAsync(CancellationToken cancellationToken)
    {
        var accountId = LocalAccountId();
        if (accountId is null) return Forbid();
        Account = await security.GetAsync(accountId.Value, cancellationToken);
        if (Account is null) return Forbid();
        if (!ModelState.IsValid) return Page();

        var result = await security.ChangePasswordAsync(
            accountId.Value, Input.CurrentPassword, Input.NewPassword, cancellationToken);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        return RedirectToPage("/Account/Security", new { status = "password-changed" });
    }

    public async Task<IActionResult> OnPostSignOutOtherSessionsAsync(CancellationToken cancellationToken)
    {
        var accountId = LocalAccountId();
        if (accountId is null) return Forbid();
        var result = await security.SignOutOtherSessionsAsync(accountId.Value, cancellationToken);
        if (!result.Succeeded)
        {
            Account = await security.GetAsync(accountId.Value, cancellationToken);
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        return RedirectToPage("/Account/Security", new { status = "sessions-signed-out" });
    }

    private async Task<LocalAccountSecurityView?> LoadAccountAsync(CancellationToken cancellationToken) =>
        LocalAccountId() is { } accountId ? await security.GetAsync(accountId, cancellationToken) : null;

    private Guid? LocalAccountId() =>
        Guid.TryParse(User.FindFirst(LocalIdentityClaimTypes.AccountId)?.Value, out var accountId) ? accountId : null;

    public sealed class ChangePasswordInput
    {
        [Required, DataType(DataType.Password), Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Compare(nameof(NewPassword)), Display(Name = "Confirm new password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
