using LinqToDB;
using SpoolManager.Infrastructure.Data;
using SpoolManager.Shared.Models;

namespace SpoolManager.Infrastructure.Repositories;

public interface ISpoolRepository
{
    Task<List<Spool>> GetAllAsync(Guid projectId, Guid? materialId = null, Guid? printerId = null, Guid? storageId = null, Guid? dryerId = null, bool? consumed = null, string? search = null);
    Task<Spool?> GetByIdAsync(Guid id, Guid projectId);
    Task<Spool?> GetByIdAsync(Guid id);
    Task<Guid> CreateAsync(Spool spool);
    Task UpdateAsync(Spool spool);
    Task<bool> TryUpdateAsync(Spool spool, DateTime expectedUpdatedAt);
    Task MarkLowNotificationAsync(Guid spoolId);
    Task DeleteAsync(Guid id);
    Task<int> GetTotalCountAsync();
    Task<List<Spool>> GetSpoolsNeedingLowNotificationAsync();
}

public class SpoolRepository : ISpoolRepository
{
    private readonly SpoolManagerDb _db;

    public SpoolRepository(SpoolManagerDb db)
    {
        _db = db;
    }

    public async Task<List<Spool>> GetAllAsync(Guid projectId, Guid? materialId = null, Guid? printerId = null, Guid? storageId = null, Guid? dryerId = null, bool? consumed = null, string? search = null)
    {
        var query = _db.Spools.Where(s => s.ProjectId == projectId);

        if (materialId.HasValue)
            query = query.Where(s => s.FilamentMaterialId == materialId.Value);

        if (printerId.HasValue)
            query = query.Where(s => s.PrinterId == printerId.Value);

        if (storageId.HasValue)
            query = query.Where(s => s.StorageLocationId == storageId.Value);

        if (dryerId.HasValue)
            query = query.Where(s => s.DryerId == dryerId.Value);

        if (consumed.HasValue)
            query = query.Where(s => consumed.Value ? s.ConsumedAt != null : s.ConsumedAt == null);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var matchingMaterialIds = await _db.FilamentMaterials
                .Where(m => m.Brand.Contains(search) || m.Type.Contains(search))
                .Select(m => m.Id).ToListAsync();
            query = query.Where(s => (s.RfidTagUid != null && s.RfidTagUid.Contains(search)) || matchingMaterialIds.Contains(s.FilamentMaterialId));
        }

        var spools = await query.OrderByDescending(s => s.CreatedAt).ToListAsync();

        if (spools.Count > 0)
        {
            var materialIds = spools.Select(s => s.FilamentMaterialId).Distinct().ToList();
            var materials = await _db.FilamentMaterials.Where(m => materialIds.Contains(m.Id)).ToListAsync();
            var materialMap = materials.ToDictionary(m => m.Id);

            var printerIds = spools.Where(s => s.PrinterId.HasValue).Select(s => s.PrinterId!.Value).Distinct().ToList();
            var printers = printerIds.Count > 0 ? await _db.Printers.Where(p => printerIds.Contains(p.Id)).ToListAsync() : new List<Printer>();
            var printerMap = printers.ToDictionary(p => p.Id);

            var storageIds = spools.Where(s => s.StorageLocationId.HasValue).Select(s => s.StorageLocationId!.Value).Distinct().ToList();
            var storages = storageIds.Count > 0 ? await _db.StorageLocations.Where(sl => storageIds.Contains(sl.Id)).ToListAsync() : new List<StorageLocation>();
            var storageMap = storages.ToDictionary(sl => sl.Id);

            var dryerIds = spools.Where(s => s.DryerId.HasValue).Select(s => s.DryerId!.Value).Distinct().ToList();
            var dryers = dryerIds.Count > 0 ? await _db.Dryers.Where(d => dryerIds.Contains(d.Id)).ToListAsync() : new List<Dryer>();
            var dryerMap = dryers.ToDictionary(d => d.Id);

            var owners = await _db.SpoolOwners.Where(o => o.ProjectId == projectId).ToListAsync();
            var ownerMap = owners.ToDictionary(o => o.Id);

            foreach (var spool in spools)
            {
                if (spool.OwnerId.HasValue && ownerMap.TryGetValue(spool.OwnerId.Value, out var owner))
                    spool.Owner = owner;
                if (materialMap.TryGetValue(spool.FilamentMaterialId, out var mat))
                    spool.FilamentMaterial = mat;
                if (spool.PrinterId.HasValue && printerMap.TryGetValue(spool.PrinterId.Value, out var prt))
                    spool.Printer = prt;
                if (spool.StorageLocationId.HasValue && storageMap.TryGetValue(spool.StorageLocationId.Value, out var stg))
                    spool.StorageLocation = stg;
                if (spool.DryerId.HasValue && dryerMap.TryGetValue(spool.DryerId.Value, out var dry))
                    spool.Dryer = dry;
            }
        }

        return spools;
    }

    public async Task<Spool?> GetByIdAsync(Guid id, Guid projectId)
    {
        var spool = await _db.Spools.FirstOrDefaultAsync(s => s.Id == id && s.ProjectId == projectId);
        return await LoadNavPropsAsync(spool);
    }

    public async Task<Spool?> GetByIdAsync(Guid id)
    {
        var spool = await _db.Spools.FirstOrDefaultAsync(s => s.Id == id);
        return await LoadNavPropsAsync(spool);
    }

    private async Task<Spool?> LoadNavPropsAsync(Spool? spool)
    {
        if (spool == null)
            return null;
        if (spool.OwnerId.HasValue)
            spool.Owner = await _db.SpoolOwners.FirstOrDefaultAsync(o => o.Id == spool.OwnerId.Value && o.ProjectId == spool.ProjectId);
        spool.FilamentMaterial = await _db.FilamentMaterials.FirstOrDefaultAsync(m => m.Id == spool.FilamentMaterialId);
        if (spool.PrinterId.HasValue)
            spool.Printer = await _db.Printers.FirstOrDefaultAsync(p => p.Id == spool.PrinterId.Value);
        if (spool.StorageLocationId.HasValue)
            spool.StorageLocation = await _db.StorageLocations.FirstOrDefaultAsync(sl => sl.Id == spool.StorageLocationId.Value);
        if (spool.DryerId.HasValue)
            spool.Dryer = await _db.Dryers.FirstOrDefaultAsync(d => d.Id == spool.DryerId.Value);
        return spool;
    }

    public async Task<Guid> CreateAsync(Spool spool)
    {
        spool.Id = Guid.NewGuid();
        spool.CreatedAt = DateTime.UtcNow;
        spool.UpdatedAt = DateTime.UtcNow;
        await _db.InsertAsync(spool);
        return spool.Id;
    }

    public async Task UpdateAsync(Spool spool) =>
        await _db.UpdateAsync(spool);

    // A printer report may arrive while an editor or inventory action is open.
    // Compare the version rather than restoring an older remaining weight.
    public async Task<bool> TryUpdateAsync(Spool spool, DateTime expectedUpdatedAt) =>
        await _db.Spools.Where(s => s.Id == spool.Id && s.ProjectId == spool.ProjectId && s.UpdatedAt == expectedUpdatedAt)
            .Set(s => s.FilamentMaterialId, spool.FilamentMaterialId)
            .Set(s => s.RfidTagUid, spool.RfidTagUid)
            .Set(s => s.InitialWeightGrams, spool.InitialWeightGrams)
            .Set(s => s.EmptySpoolWeightGrams, spool.EmptySpoolWeightGrams)
            .Set(s => s.RemainingWeightGrams, spool.RemainingWeightGrams)
            .Set(s => s.RemainingPercent, spool.RemainingPercent)
            .Set(s => s.OwnerId, spool.OwnerId)
            .Set(s => s.PrinterId, spool.PrinterId)
            .Set(s => s.StorageLocationId, spool.StorageLocationId)
            .Set(s => s.DryerId, spool.DryerId)
            .Set(s => s.OpenedAt, spool.OpenedAt)
            .Set(s => s.RepackagedAt, spool.RepackagedAt)
            .Set(s => s.ReopenedAt, spool.ReopenedAt)
            .Set(s => s.DriedAt, spool.DriedAt)
            .Set(s => s.ConsumedAt, spool.ConsumedAt)
            .Set(s => s.PurchasedAt, spool.PurchasedAt)
            .Set(s => s.PurchasePrice, spool.PurchasePrice)
            .Set(s => s.ReorderUrl, spool.ReorderUrl)
            .Set(s => s.Notes, spool.Notes)
            .Set(s => s.LowSpoolNotifiedAt, spool.LowSpoolNotifiedAt)
            .Set(s => s.UpdatedAt, spool.UpdatedAt)
            .UpdateAsync() == 1;

    public async Task MarkLowNotificationAsync(Guid spoolId) =>
        await _db.Spools.Where(s => s.Id == spoolId)
            .Set(s => s.LowSpoolNotifiedAt, DateTime.UtcNow).UpdateAsync();

    public async Task DeleteAsync(Guid id) =>
        await _db.Spools.Where(s => s.Id == id).DeleteAsync();

    public async Task<int> GetTotalCountAsync() =>
        await _db.Spools.CountAsync();

    public async Task<List<Spool>> GetSpoolsNeedingLowNotificationAsync()
    {
        var spools = await _db.Spools
            .Where(s => s.ConsumedAt == null && s.RemainingPercent <= 15 && s.LowSpoolNotifiedAt == null)
            .ToListAsync();

        foreach (var spool in spools)
            spool.FilamentMaterial = await _db.FilamentMaterials
                .FirstOrDefaultAsync(m => m.Id == spool.FilamentMaterialId);

        return spools;
    }
}
