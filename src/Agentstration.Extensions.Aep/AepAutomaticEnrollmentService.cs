using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.Logging;

namespace Agentstration.Extensions.Aep;

public sealed partial class AepAutomaticEnrollmentService(
    ExtensionEnrollmentPolicyService policies,
    AepEnrollmentService enrollment,
    AepEnrollmentSettingsService settings,
    IResourceStore store,
    IRequestContextScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<AepAutomaticEnrollmentService> logger)
{
    private volatile bool active;

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        await policies.ValidateTargetsAsync(cancellationToken);
        active = true;
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        if (!active) return;
        using var system = scopes.PushSystem();
        if (!await settings.IsEnabledAsync(AepEnrollmentMode.SharedKeyFile, cancellationToken)) return;
        const int pageSize = 100;
        for (var offset = 0; ; offset += pageSize)
        {
            var requests = await store.ListExactAsync<AepEnrollmentRequestResource>(ResourceScopeRef.Instance,
                ExtensionKinds.AepEnrollmentRequest, offset, pageSize, cancellationToken);
            foreach (var request in requests)
            {
                var definition = request.Value.Definition;
                if (definition.EnrollmentMode != AepEnrollmentMode.SharedKeyFile) continue;
                var newCandidate = definition.State == AepEnrollmentState.Pending && definition.TargetScopeRef is null;
                var retry = definition.AutomaticEnrollmentPolicy is not null
                    && (definition.State == AepEnrollmentState.VerificationFailed
                        || (definition.State == AepEnrollmentState.Verifying
                            && definition.AutomaticEnrollmentLeaseExpiresAt <= timeProvider.GetUtcNow()));
                if (!newCandidate && !retry) continue;
                try
                {
                    var target = await policies.SelectAsync(definition.ExtensionId, definition.InstanceId, cancellationToken);
                    if (target is not null)
                        await enrollment.AssignAutomaticallyAsync(definition.InstanceId, target, cancellationToken);
                }
                catch (ResourceConcurrencyException) { }
                catch (AepEnrollmentException exception)
                {
                    EnrollmentFailed(logger, definition.InstanceId, exception.Code);
                }
            }
            if (requests.Count < pageSize) return;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic AEP enrollment for installation {InstanceId} could not complete: {ReasonCode}.")]
    private static partial void EnrollmentFailed(ILogger logger, Guid instanceId, string reasonCode);
}
