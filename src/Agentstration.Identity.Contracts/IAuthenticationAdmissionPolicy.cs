namespace Agentstration.Identity.Contracts;

/// <summary>Additional host admission rules, evaluated after credential validation.</summary>
public interface IAuthenticationAdmissionPolicy
{
    Task<bool> CanSignInAsync(Guid principalId, CancellationToken cancellationToken);
}
