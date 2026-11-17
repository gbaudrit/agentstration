using Agentstration.Asp.Abstractions;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Product payload limits are distinct from ASP wire chunk limits.</summary>
internal static class StorageContentValidation
{
    internal static AspAccessPolicy WirePolicy(AspAccessPolicy policy, int chunkBytes = AspProtocol.MaxContentBytes) =>
        policy with
        {
            MaxReadBytes = Math.Min(policy.MaxReadBytes, chunkBytes),
            MaxWriteBytes = Math.Min(policy.MaxWriteBytes, chunkBytes),
            MaxPageSize = Math.Min(policy.MaxPageSize, AspProtocol.MaxPageSize)
        };

    internal static void Policy(AspAccessPolicy policy)
    {
        StorageContentOptions.ValidatePolicy(new(MaximumReadBytes: policy.MaxReadBytes,
            MaximumWriteBytes: policy.MaxWriteBytes, MaximumPageSize: policy.MaxPageSize));
        AspValidation.Policy(WirePolicy(policy));
    }

    internal static void Context(AspAccessContext context)
    {
        Policy(context.Policy);
        AspValidation.Context(context with { Policy = WirePolicy(context.Policy) });
    }
}
