using LinqToDB;
using SpoolManager.Infrastructure.Data;
using SpoolManager.Shared.Models;

namespace SpoolManager.Infrastructure.Repositories;

public interface IOwnerRepository
{
    Task<List<SpoolOwner>> GetAllAsync(Guid projectId);
    Task<SpoolOwner?> GetByIdAsync(Guid id, Guid projectId);
    Task CreateAsync(SpoolOwner owner);
    Task UpdateAsync(SpoolOwner owner);
}

public class OwnerRepository(SpoolManagerDb db) : IOwnerRepository
{
    public Task<List<SpoolOwner>> GetAllAsync(Guid projectId) =>
        db.SpoolOwners.Where(o => o.ProjectId == projectId).OrderBy(o => o.Name).ToListAsync();

    public Task<SpoolOwner?> GetByIdAsync(Guid id, Guid projectId) =>
        db.SpoolOwners.FirstOrDefaultAsync(o => o.Id == id && o.ProjectId == projectId);

    public async Task CreateAsync(SpoolOwner owner) => await db.InsertAsync(owner);

    public async Task UpdateAsync(SpoolOwner owner) =>
        await db.SpoolOwners.Where(o => o.Id == owner.Id && o.ProjectId == owner.ProjectId)
            .Set(o => o.Name, owner.Name).UpdateAsync();
}
