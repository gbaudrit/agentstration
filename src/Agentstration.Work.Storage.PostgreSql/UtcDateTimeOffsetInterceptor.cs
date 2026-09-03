using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agentstration.Work.Storage.PostgreSql;

internal sealed class UtcDateTimeOffsetInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Normalize(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Normalize(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Normalize(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries().Where(value => value.State is EntityState.Added or EntityState.Modified))
            foreach (var property in entry.Properties)
                if (property.CurrentValue is DateTimeOffset value && value.Offset != TimeSpan.Zero)
                    property.CurrentValue = value.ToUniversalTime();
    }
}
