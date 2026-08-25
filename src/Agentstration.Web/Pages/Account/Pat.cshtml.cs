using System.ComponentModel.DataAnnotations;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Agentstration.Web.Pages.Account;

[Authorize(Policy = AgentstrationPolicies.InteractiveUser)]
public sealed class PatModel(
    PersonalAccessTokenService personalAccessTokens,
    IdentityExperienceService identity,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public CreateInput Input { get; set; } = new();

    public IReadOnlyList<PersonalAccessToken> Tokens { get; private set; } = [];
    public IReadOnlyList<ConsoleWorkspaceView> Workspaces { get; private set; } = [];
    public IReadOnlySet<string> SupportedPermissions => PersonalAccessTokenService.SupportedPermissions;
    public string? CreatedToken { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (!ModelState.IsValid) return Page();

        try
        {
            var created = await personalAccessTokens.CreateAsync(
                new(
                    Input.Name,
                    Input.WorkspaceId,
                    Input.Permissions,
                    timeProvider.GetUtcNow().AddDays(Input.ExpiresInDays)),
                cancellationToken);
            CreatedToken = created.Token;
            Input = new();
            await LoadAsync(cancellationToken);
            return Page();
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        catch (AuthorizationDeniedException exception)
        {
            ModelState.AddModelError(string.Empty, $"Permission denied: {exception.Message}");
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        await personalAccessTokens.RevokeCurrentAsync(tokenId, cancellationToken);
        return RedirectToPage("/Account/Pat");
    }

    public async Task<IActionResult> OnPostRevokeAllAsync(CancellationToken cancellationToken)
    {
        await personalAccessTokens.RevokeAllCurrentAsync(cancellationToken);
        return RedirectToPage("/Account/Pat");
    }

    public string Status(PersonalAccessToken token)
    {
        if (token.RevokedAt is not null) return "Revoked";
        return token.ExpiresAt <= timeProvider.GetUtcNow() ? "Expired" : "Active";
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Tokens = await personalAccessTokens.ListCurrentAsync(cancellationToken);
        Workspaces = (await identity.GetContextAsync(cancellationToken)).AvailableWorkspaces;
        if (Input.WorkspaceId == Guid.Empty) Input.WorkspaceId = Workspaces.FirstOrDefault()?.Id ?? Guid.Empty;
    }

    public sealed class CreateInput
    {
        [Required, StringLength(80, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public Guid WorkspaceId { get; set; }

        [Range(1, 365), Display(Name = "Expires in")]
        public int ExpiresInDays { get; set; } = 90;

        [MinLength(1, ErrorMessage = "Select at least one permission.")]
        public List<string> Permissions { get; set; } = [];
    }
}
