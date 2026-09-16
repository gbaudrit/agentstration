using System.ComponentModel.DataAnnotations;
using Agentstration.Console.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel(
    IBffSessionAuthorityClient authority,
    IOptions<BffSessionOptions> sessionOptions,
    TimeProvider timeProvider,
    IStringLocalizer<AuthStrings> localizer) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        ReturnUrl = NormalizeReturnUrl(ReturnUrl);
        return User.Identity?.IsAuthenticated == true ? LocalRedirect(ReturnUrl) : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ReturnUrl = NormalizeReturnUrl(ReturnUrl);
        if (!ModelState.IsValid) return Page();

        BffLocalAuthenticationResult result;
        try
        {
            result = await authority.AuthenticateLocalAsync(
                Input.UserName.Trim(),
                Input.Password,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, localizer["AuthorityUnavailable"]);
            return Page();
        }

        if (result.Outcome != BffLocalAuthenticationOutcome.Succeeded || result.Identity is null)
        {
            ModelState.AddModelError(string.Empty, result.Outcome == BffLocalAuthenticationOutcome.LockedOut
                ? localizer["AccountLocked"].Value
                : result.Outcome == BffLocalAuthenticationOutcome.Unavailable
                    ? localizer["AuthorityUnavailable"].Value
                    : localizer["InvalidCredentials"].Value);
            return Page();
        }

        await HttpContext.SignOutAsync(ConsoleAuthenticationDefaults.Scheme);
        var now = timeProvider.GetUtcNow();
        var absoluteExpiry = Input.RememberMe
            ? now.AddDays(sessionOptions.Value.RememberedAbsoluteLifetimeDays)
            : now.AddHours(sessionOptions.Value.AbsoluteLifetimeHours);
        await HttpContext.SignInAsync(
            ConsoleAuthenticationDefaults.Scheme,
            BffSessionClaims.Create(result.Identity),
            new AuthenticationProperties
            {
                AllowRefresh = true,
                IsPersistent = Input.RememberMe,
                IssuedUtc = now,
                ExpiresUtc = absoluteExpiry
            });
        return LocalRedirect(ReturnUrl);
    }

    private string NormalizeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";

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
