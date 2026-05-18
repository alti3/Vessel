using Microsoft.EntityFrameworkCore;
using Vessel.Application.Persistence;
using Vessel.Domain.Common;

namespace Vessel.Infrastructure.Persistence;

internal sealed class EfRepository<TEntity, TId> : IRepository<TEntity, TId>
    where TEntity : Entity<TId>
    where TId : notnull
{
    private readonly DbSet<TEntity> _set;

    public EfRepository(DbContext context)
    {
        _set = context.Set<TEntity>();
    }

    public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default)
    {
        return _set.FindAsync([id], cancellationToken).AsTask();
    }

    public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        return _set.AddAsync(entity, cancellationToken).AsTask();
    }

    public void Remove(TEntity entity)
    {
        _set.Remove(entity);
    }
}
