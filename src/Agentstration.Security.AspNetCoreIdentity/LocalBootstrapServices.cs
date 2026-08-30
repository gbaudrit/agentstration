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

public sealed class LocalBootstrapCoordinator(
    UserManager<LocalIdentityUser> users,
    LocalIdentityDbContext database,
    IInitialPrincipalProvisioner provisioner,
    IInitialTopologyProvisioner topologyProvisioner,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider,
    LocalBootstrapLock bootstrapLock)
{
    public async Task<bool> IsInitializedAsync(CancellationToken cancellationToken) =>
        await database.BootstrapStates.AsNoTracking().AnyAsync(cancellationToken)
        || await users.Users.AnyAsync(cancellationToken);

    public async Task<LocalBootstrapResult> BootstrapAsync(LocalBootstrapRequest request, CancellationToken cancellationToken)
    {
        await bootstrapLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (await IsInitializedAsync(cancellationToken))
                return new(false, null, ["This Agentstration instance is already initialized."]);
            if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length is < 3 or > 64)
                return new(false, null, ["UserName must contain between 3 and 64 characters."]);
            if (string.IsNullOrWhiteSpace(request.Password))
                return new(false, null, ["Password is required."]);
            if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length is < 2 or > 120)
                return new(false, null, ["DisplayName must contain between 2 and 120 characters."]);

            var accountId = Guid.NewGuid();
            var principalId = Guid.NewGuid();
            var account = new LocalIdentityUser
            {
                Id = accountId,
                UserName = request.UserName.Trim(),
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
                LockoutEnabled = true
            };
            var created = await users.CreateAsync(account, request.Password);
            if (!created.Succeeded)
                return new(false, null, created.Errors.Select(error => error.Description).ToArray());

            try
            {
                var provisioned = await provisioner.ProvisionAsync(new InitialPrincipalProvisioning(
                    accountId,
                    principalId,
                    request.DisplayName.Trim(),
                    account.Email), cancellationToken);
                InitialTopologyProvisioningResult? topology = null;
                if (request.Topology is { } requestedTopology)
                    topology = await topologyProvisioner.ProvisionAsync(new(
                        provisioned.Principal.Id,
                        requestedTopology.TenantName,
                        requestedTopology.TenantDisplayName,
                        requestedTopology.WorkspaceName,
                        requestedTopology.WorkspaceDisplayName), cancellationToken);
                database.BootstrapStates.Add(new BootstrapState
                {
                    Id = 1,
                    PrincipalId = principalId,
                    CompletedAt = timeProvider.GetUtcNow()
                });
                await database.SaveChangesAsync(cancellationToken);
                await audit.WriteAsync(new(
                    SecurityAuditActions.InstanceBootstrapped,
                    ActorPrincipalId: principalId,
                    TargetPrincipalId: principalId,
                    TargetAccountId: accountId,
                    TenantId: topology?.Tenant.Id,
                    WorkspaceId: topology?.Workspace.Id), cancellationToken);
                return new(true, principalId, []);
            }
            catch
            {
                await users.DeleteAsync(account);
                throw;
            }
        }
        finally
        {
            bootstrapLock.Semaphore.Release();
        }
    }
}

