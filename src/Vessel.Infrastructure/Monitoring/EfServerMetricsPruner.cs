using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring.HostMetrics;
using Vessel.Application.Persistence;
using Vessel.Domain.Monitoring;
using Vessel.Infrastructure.Persistence;

namespace Vessel.Infrastructure.Monitoring;

/// <summary>Set-based metric retention (<c>DELETE … WHERE</c>) that bypasses the change tracker.</summary>
public sealed class EfServerMetricsPruner(IVesselDbContext dbContext) : IServerMetricsPruner
{
    public async Task<int> PruneAsync(DateTimeOffset rawBefore, DateTimeOffset rollupBefore,
        DateTimeOffset containersBefore, CancellationToken cancellationToken = default)
    {
        if (dbContext is not VesselDbContext context)
            return await new QueryableServerMetricsPruner(dbContext).PruneAsync(rawBefore, rollupBefore,
                containersBefore, cancellationToken);

        var deleted = await context.ServerMetricSampleSet
            .Where(sample => sample.Resolution == MetricResolution.Raw && sample.BucketStart < rawBefore)
            .ExecuteDeleteAsync(cancellationToken);
        deleted += await context.ServerMetricSampleSet
            .Where(sample => sample.Resolution == MetricResolution.Hourly && sample.BucketStart < rollupBefore)
            .ExecuteDeleteAsync(cancellationToken);
        deleted += await context.ContainerMetricSampleSet
            .Where(sample => sample.CollectedAt < containersBefore)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted;
    }
}
