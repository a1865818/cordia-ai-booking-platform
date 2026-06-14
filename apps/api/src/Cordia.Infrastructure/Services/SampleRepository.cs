using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Services;

public class SampleRepository : ISampleRepository
{
    private readonly ApplicationDbContext _dbContext;

    public SampleRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<SampleEntity>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Samples.AsNoTracking().ToListAsync(cancellationToken);
    }
}
