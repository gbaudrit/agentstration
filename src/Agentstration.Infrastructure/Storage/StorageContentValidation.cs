using Agentstration.Asc.Abstractions;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Product payload limits are distinct from ASC wire chunk limits.</summary>
internal static class StorageContentValidation
{
    internal static AscAccessPolicy WirePolicy(AscAccessPolicy policy, int chunkBytes = AscContract.MaxContentBytes) =>
        policy with
        {
            MaxReadBytes = Math.Min(policy.MaxReadBytes, chunkBytes),
            MaxWriteBytes = Math.Min(policy.MaxWriteBytes, chunkBytes),
            MaxPageSize = Math.Min(policy.MaxPageSize, AscContract.MaxPageSize)
        };

    internal static void Policy(AscAccessPolicy policy)
    {
        StorageContentOptions.ValidatePolicy(new(MaximumReadBytes: policy.MaxReadBytes,
            MaximumWriteBytes: policy.MaxWriteBytes, MaximumPageSize: policy.MaxPageSize));
        AscValidation.Policy(WirePolicy(policy));
    }

    internal static void Context(AscAccessContext context)
    {
        Policy(context.Policy);
        AscValidation.Context(context with { Policy = WirePolicy(context.Policy) });
    }
}
