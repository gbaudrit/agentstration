using System.Security.Claims;
using Agentstration.Management.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Security.AspNetCoreIdentity;

public sealed class LocalAccountPrincipalResolver(
    UserManager<LocalIdentityUser> users,
    IPrincipalResolver principals) : ILocalAccountPrincipalResolver
{
    public async Task<Principal?> ResolveByUserNameAsync(string userName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await users.FindByNameAsync(userName.Trim());
        return account is null ? null : await principals.ResolveLocalAsync(account.Id, cancellationToken);
    }
}

public sealed class LocalAccountAdministrationService(
    UserManager<LocalIdentityUser> users,
    IIdentityStore identityStore,
    ILocalPrincipalProvisioner provisioner,
    ICurrentRequestContext requestContext,
    IPlatformAuthorizationService platformAuthorization,
    IPlatformAdministratorPolicy platformAdministratorPolicy,
    ISecurityAuditWriter audit)
{
    public async Task<IReadOnlyList<LocalAccountView>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(cancellationToken);
        var result = new List<LocalAccountView>();
        foreach (var account in await users.Users.AsNoTracking().OrderBy(value => value.UserName).ToArrayAsync(cancellationToken))
        {
            var link = await identityStore.FindLocalIdentityAsync(account.Id, cancellationToken);
            if (link is null) continue;
            var principal = await identityStore.GetPrincipalAsync(link.PrincipalId, cancellationToken);
            if (principal is null) continue;
            result.Add(await ViewAsync(account, principal, cancellationToken));
        }
        return result;
    }

    public async Task<(LocalAccountView? Account, IReadOnlyList<string> Errors)> CreateAsync(CreateLocalAccountRequest request, CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length is < 3 or > 64)
            return (null, ["UserName must contain between 3 and 64 characters."]);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length is < 2 or > 120)
            return (null, ["DisplayName must contain between 2 and 120 characters."]);
        if (string.IsNullOrWhiteSpace(request.Password)) return (null, ["Password is required."]);

        var account = new LocalIdentityUser
        {
            Id = Guid.NewGuid(),
            UserName = request.UserName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            LockoutEnabled = true
        };
        var created = await users.CreateAsync(account, request.Password);
        if (!created.Succeeded) return (null, created.Errors.Select(error => error.Description).ToArray());
        try
        {
            var principal = await provisioner.ProvisionAsync(new LocalPrincipalProvisioning(
                account.Id,
                Guid.NewGuid(),
                request.WorkspaceId,
                request.Role,
                request.DisplayName.Trim(),
                account.Email), cancellationToken);
            await audit.WriteAsync(new(
                SecurityAuditActions.LocalAccountCreated,
                TargetPrincipalId: principal.Id,
                TargetAccountId: account.Id,
                WorkspaceId: request.WorkspaceId), cancellationToken);
            return (await ViewAsync(account, principal, cancellationToken), []);
        }
        catch
        {
            await users.DeleteAsync(account);
            throw;
        }
    }

    public async Task<LocalAccountView> SetEnabledAsync(Guid accountId, bool enabled, CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(cancellationToken);
        var account = await users.FindByIdAsync(accountId.ToString("D"))
            ?? throw new ArgumentException("The local account does not exist.", nameof(accountId));
        var link = await identityStore.FindLocalIdentityAsync(account.Id, cancellationToken)
            ?? throw new InvalidOperationException("The local account is not linked to a Principal.");
        var principal = await identityStore.GetPrincipalAsync(link.PrincipalId, cancellationToken)
            ?? throw new InvalidOperationException("The linked Principal does not exist.");
        IAsyncDisposable? lifecycleLease = null;
        try
        {
            if (!enabled) lifecycleLease = await platformAdministratorPolicy.AcquireDisableLeaseAsync(principal.Id, cancellationToken);

            account.LockoutEnabled = true;
            account.LockoutEnd = enabled ? null : DateTimeOffset.MaxValue;
            var updated = await users.UpdateAsync(account);
            if (!updated.Succeeded) throw new InvalidOperationException(string.Join("; ", updated.Errors.Select(error => error.Description)));
            await users.UpdateSecurityStampAsync(account);
            principal = principal with { Status = enabled ? PrincipalStatus.Active : PrincipalStatus.Disabled };
            await identityStore.UpdatePrincipalAsync(principal, cancellationToken);
            await audit.WriteAsync(new(
                enabled ? SecurityAuditActions.LocalAccountEnabled : SecurityAuditActions.LocalAccountDisabled,
                TargetPrincipalId: principal.Id,
                TargetAccountId: account.Id), cancellationToken);
            return await ViewAsync(account, principal, cancellationToken);
        }
        finally
        {
            if (lifecycleLease is not null) await lifecycleLease.DisposeAsync();
        }
    }

    private async Task EnsurePlatformAdministratorAsync(CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized || !await platformAuthorization.IsPlatformAdministratorAsync(requestContext.Current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
    }

    private async Task<LocalAccountView> ViewAsync(LocalIdentityUser account, Principal principal, CancellationToken cancellationToken) =>
        new(account.Id, principal.Id, account.UserName ?? account.Id.ToString("D"), principal.DisplayName, principal.Email,
            principal.Status, account.AccessFailedCount, account.LockoutEnd,
            await platformAuthorization.IsPlatformAdministratorAsync(principal.Id, cancellationToken));
}

public sealed class LocalAccountSecurityService(
    UserManager<LocalIdentityUser> users,
    SignInManager<LocalIdentityUser> signIn,
    IIdentityStore identityStore,
    ISecurityAuditWriter audit)
{
    public async Task<LocalAccountSecurityView?> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await users.FindByIdAsync(accountId.ToString("D"));
        return account is null
            ? null
            : new(account.Id, account.UserName ?? account.Id.ToString("D"), account.Email);
    }

    public async Task<LocalAccountSecurityResult> ChangePasswordAsync(
        Guid accountId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await users.FindByIdAsync(accountId.ToString("D"));
        if (account is null)
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalPasswordChanged, SecurityAuditOutcome.Failed,
                TargetAccountId: accountId, ReasonCode: "account-not-found"), cancellationToken);
            return new(false, ["The local account no longer exists."]);
        }
        var changed = await users.ChangePasswordAsync(account, currentPassword, newPassword);
        if (!changed.Succeeded)
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalPasswordChanged, SecurityAuditOutcome.Failed,
                TargetPrincipalId: await PrincipalIdAsync(accountId, cancellationToken), TargetAccountId: accountId,
                ReasonCode: "credential-rejected"), cancellationToken);
            return new(false, changed.Errors.Select(error => error.Description).ToArray());
        }

        await signIn.RefreshSignInAsync(account);
        await audit.WriteAsync(new(SecurityAuditActions.LocalPasswordChanged,
            TargetPrincipalId: await PrincipalIdAsync(accountId, cancellationToken), TargetAccountId: accountId), cancellationToken);
        return new(true, []);
    }

    public async Task<LocalAccountSecurityResult> SignOutOtherSessionsAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await users.FindByIdAsync(accountId.ToString("D"));
        if (account is null)
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalSessionsRevoked, SecurityAuditOutcome.Failed,
                TargetAccountId: accountId, ReasonCode: "account-not-found"), cancellationToken);
            return new(false, ["The local account no longer exists."]);
        }
        var updated = await users.UpdateSecurityStampAsync(account);
        if (!updated.Succeeded)
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalSessionsRevoked, SecurityAuditOutcome.Failed,
                TargetPrincipalId: await PrincipalIdAsync(accountId, cancellationToken), TargetAccountId: accountId,
                ReasonCode: "security-stamp-update-failed"), cancellationToken);
            return new(false, updated.Errors.Select(error => error.Description).ToArray());
        }

        await signIn.RefreshSignInAsync(account);
        await audit.WriteAsync(new(SecurityAuditActions.LocalSessionsRevoked,
            TargetPrincipalId: await PrincipalIdAsync(accountId, cancellationToken), TargetAccountId: accountId), cancellationToken);
        return new(true, []);
    }

    private async Task<Guid?> PrincipalIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await identityStore.FindLocalIdentityAsync(accountId, cancellationToken))?.PrincipalId;
}

public sealed class LocalAuthenticationService(
    UserManager<LocalIdentityUser> users,
    SignInManager<LocalIdentityUser> signIn,
    IIdentityStore identityStore,
    ISecurityAuditWriter audit)
{
    public async Task<LocalLoginOutcome> PasswordSignInAsync(
        string userName,
        string password,
        bool rememberMe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalLogin, SecurityAuditOutcome.Failed,
                ReasonCode: "missing-credentials"), cancellationToken);
            return LocalLoginOutcome.Failed;
        }
        var normalized = userName.Trim();
        var result = await signIn.PasswordSignInAsync(normalized, password, rememberMe, lockoutOnFailure: true);
        var account = await users.FindByNameAsync(normalized);
        var principalId = account is null
            ? null
            : (await identityStore.FindLocalIdentityAsync(account.Id, cancellationToken))?.PrincipalId;
        var outcome = result.Succeeded ? LocalLoginOutcome.Succeeded : result.IsLockedOut ? LocalLoginOutcome.LockedOut : LocalLoginOutcome.Failed;
        await audit.WriteAsync(new(
            SecurityAuditActions.LocalLogin,
            result.Succeeded ? SecurityAuditOutcome.Succeeded : SecurityAuditOutcome.Failed,
            ActorPrincipalId: result.Succeeded ? principalId : null,
            TargetPrincipalId: principalId,
            TargetAccountId: account?.Id,
            ReasonCode: outcome switch
            {
                LocalLoginOutcome.LockedOut => "locked-out",
                LocalLoginOutcome.Failed => "invalid-credentials",
                _ => null
            }), cancellationToken);
        return outcome;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await signIn.SignOutAsync();
        await audit.WriteAsync(new(SecurityAuditActions.LocalLogout), cancellationToken);
    }
}

