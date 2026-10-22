using System.Data;
using System.Linq.Expressions;
using LinqToDB;
using SpoolManager.Infrastructure.Data;
using SpoolManager.Shared.DTOs.Backup;
using SpoolManager.Shared.Models;

namespace SpoolManager.Infrastructure.Services;

public interface IBackupService
{
    Task<InventoryBackupDocument> ExportAsync(Guid userId, bool includeCatalogSetting);
    Task<BackupImportResultDto> ImportAsync(InventoryBackupDocument document, Guid userId, bool applyCatalogSetting);
}

public class BackupValidationException(string message) : Exception(message) { }
public class BackupConflictException(string message) : Exception(message) { }

public class BackupService(SpoolManagerDb db, IMaterialCatalogSettings catalogSettings) : IBackupService
{
    public async Task<InventoryBackupDocument> ExportAsync(Guid userId, bool includeCatalogSetting)
    {
        // One consistent snapshot includes relationships changed while exporting.
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var projectIds = await db.ProjectMembers.Where(m => m.UserId == userId && m.Role == "admin")
            .Select(m => m.ProjectId).ToListAsync();
        if (projectIds.Count == 0)
            throw new BackupValidationException("There are no projects you administer to export.");

        var projects = await db.Projects.Where(p => projectIds.Contains(p.Id)).OrderBy(p => p.Name).ToListAsync();
        var spools = await db.Spools.Where(s => projectIds.Contains(s.ProjectId)).OrderBy(s => s.Id).ToListAsync();
        var favorites = await db.UserMaterialFavorites.Where(f => f.UserId == userId).ToListAsync();
        var referencedIds = spools.Select(s => s.FilamentMaterialId).Concat(favorites.Select(f => f.MaterialId)).Distinct().ToList();
        var materials = await db.FilamentMaterials.Where(m =>
            (m.ProjectId.HasValue && projectIds.Contains(m.ProjectId.Value)) ||
            (m.ProjectId == null && referencedIds.Contains(m.Id))).OrderBy(m => m.Id).ToListAsync();
        // Global click totals describe server-wide usage, not the caller's inventory.
        foreach (var material in materials.Where(m => m.ProjectId == null))
            material.ReorderClickCount = 0;
        var materialIds = materials.Select(m => m.Id).ToHashSet();

        var document = new InventoryBackupDocument
        {
            Format = InventoryBackupDocument.CurrentFormat,
            Version = InventoryBackupDocument.CurrentVersion,
            ExportedAt = DateTime.UtcNow,
            Projects = projects,
            Materials = materials,
            Owners = await db.SpoolOwners.Where(o => projectIds.Contains(o.ProjectId)).OrderBy(o => o.Id).ToListAsync(),
            Spools = spools.Select(ToBackupSpool).ToList(),
            Printers = await db.Printers.Where(p => projectIds.Contains(p.ProjectId)).OrderBy(p => p.Id).ToListAsync(),
            StorageLocations = await db.StorageLocations.Where(s => projectIds.Contains(s.ProjectId)).OrderBy(s => s.Id).ToListAsync(),
            Dryers = await db.Dryers.Where(d => projectIds.Contains(d.ProjectId)).OrderBy(d => d.Id).ToListAsync(),
            MaterialFavorites = favorites.Where(f => materialIds.Contains(f.MaterialId))
                .Select(f => new BackupFavoriteDto { MaterialId = f.MaterialId, CreatedAt = f.CreatedAt }).ToList(),
            OpenFilamentDbEnabled = includeCatalogSetting ? await catalogSettings.IsOpenFilamentDbEnabledAsync() : null
        };
        Validate(document);
        await transaction.CommitAsync();
        return document;
    }

    public async Task<BackupImportResultDto> ImportAsync(InventoryBackupDocument document, Guid userId, bool applyCatalogSetting)
    {
        Validate(document);
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.Serializable);
        // Preflight all collisions before inserting anything. The serializable transaction
        // also protects this check against concurrent restores into the same target.
        await EnsureNewAsync(db.Projects, document.Projects.Select(x => x.Id), x => x.Id, "project");
        await EnsureNewAsync(db.FilamentMaterials, document.Materials.Select(x => x.Id), x => x.Id, "material");
        await EnsureNewAsync(db.SpoolOwners, document.Owners.Select(x => x.Id), x => x.Id, "owner");
        await EnsureNewAsync(db.Spools, document.Spools.Select(x => x.Id), x => x.Id, "spool");
        await EnsureNewAsync(db.Printers, document.Printers.Select(x => x.Id), x => x.Id, "printer");
        await EnsureNewAsync(db.StorageLocations, document.StorageLocations.Select(x => x.Id), x => x.Id, "storage location");
        await EnsureNewAsync(db.Dryers, document.Dryers.Select(x => x.Id), x => x.Id, "dryer");

        foreach (var project in document.Projects)
        {
            await db.InsertAsync(project);
            await db.InsertAsync(new ProjectMember
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, UserId = userId,
                Role = "admin", JoinedAt = DateTime.UtcNow
            });
        }
        foreach (var material in document.Materials) await db.InsertAsync(material);
        foreach (var owner in document.Owners) await db.InsertAsync(owner);
        foreach (var printer in document.Printers) await db.InsertAsync(printer);
        foreach (var location in document.StorageLocations) await db.InsertAsync(location);
        foreach (var dryer in document.Dryers) await db.InsertAsync(dryer);
        foreach (var spool in document.Spools) await db.InsertAsync(FromBackupSpool(spool));
        foreach (var favorite in document.MaterialFavorites)
            await db.InsertAsync(new UserMaterialFavorite
            {
                UserId = userId, MaterialId = favorite.MaterialId, CreatedAt = favorite.CreatedAt
            });
        var catalogApplied = applyCatalogSetting && document.OpenFilamentDbEnabled.HasValue;
        if (catalogApplied)
            await catalogSettings.SetOpenFilamentDbEnabledAsync(document.OpenFilamentDbEnabled!.Value);
        await transaction.CommitAsync();
        return new BackupImportResultDto
        {
            ProjectIds = document.Projects.Select(p => p.Id).ToList(),
            ProjectCount = document.Projects.Count, MaterialCount = document.Materials.Count,
            OwnerCount = document.Owners.Count, SpoolCount = document.Spools.Count,
            PrinterCount = document.Printers.Count, StorageLocationCount = document.StorageLocations.Count,
            DryerCount = document.Dryers.Count, FavoriteCount = document.MaterialFavorites.Count,
            CatalogSettingApplied = catalogApplied
        };
    }

    private static async Task EnsureNewAsync<T>(ITable<T> table, IEnumerable<Guid> ids,
        Expression<Func<T, Guid>> selectId, string kind) where T : class
    {
        foreach (var batch in ids.Chunk(500))
        {
            // List.Contains remains a translatable collection lookup on .NET 10;
            // array.Contains can bind to the newer ReadOnlySpan overload instead.
            var batchIds = batch.ToList();
            if (await table.Select(selectId).AnyAsync(id => batchIds.Contains(id)))
                throw new BackupConflictException($"A {kind} ID from this backup already exists. Nothing was imported. Restore into a fresh instance; existing inventory is never overwritten.");
        }
    }

    private static void Validate(InventoryBackupDocument d)
    {
        if (d.Format != InventoryBackupDocument.CurrentFormat || d.Version != InventoryBackupDocument.CurrentVersion)
            throw new BackupValidationException("Unsupported backup format or version. Select a SpoolHero full inventory backup (version 1).");
        var projects = Index(d.Projects, x => x.Id, "projects");
        var materials = Index(d.Materials, x => x.Id, "materials");
        var owners = Index(d.Owners, x => x.Id, "owners");
        var spools = Index(d.Spools, x => x.Id, "spools");
        var printers = Index(d.Printers, x => x.Id, "printers");
        var locations = Index(d.StorageLocations, x => x.Id, "storage locations");
        var dryers = Index(d.Dryers, x => x.Id, "dryers");
        var favorites = Index(d.MaterialFavorites, x => x.MaterialId, "favorites");
        if (projects.Count == 0)
            throw new BackupValidationException("The backup does not contain any projects.");
        if ((long)projects.Count + materials.Count + owners.Count + spools.Count + printers.Count + locations.Count + dryers.Count + favorites.Count > 100_000)
            throw new BackupValidationException("This backup exceeds the limit of 100,000 inventory records.");
        Date(d.ExportedAt, "export date");
        foreach (var p in projects.Values)
        {
            Text(p.Name, 200, "project name", true); Text(p.Description, 2000, "project description");
            Date(p.CreatedAt, "project creation date"); Date(p.UpdatedAt, "project update date");
        }
        foreach (var m in materials.Values)
        {
            if (m.ProjectId.HasValue) Project(m.ProjectId.Value);
            Text(m.Type, 100, "material type", true); Text(m.Brand, 200, "material brand");
            if (m.Brand == null) throw new BackupValidationException("Material brand cannot be null.");
            Text(m.ColorName, 200, "material color name"); Text(m.Notes, 2000, "material notes");
            Text(m.ReorderUrl, 2000, "material reorder URL");
            Text(m.OfdFilamentId, 40, "OFD filament ID"); Text(m.OfdVariantId, 40, "OFD variant ID");
            if (m.ColorHex == null || !System.Text.RegularExpressions.Regex.IsMatch(m.ColorHex, "^#?[0-9a-fA-F]{6}$"))
                throw new BackupValidationException("A material has an invalid color hex value.");
            Number(m.DiameterMm, 999.99m, 2, "material diameter");
            Number(m.DensityGCm3, 99.9999m, 4, "material density");
            Number(m.PricePerKg, 99_999_999.99m, 2, "material price");
            Number(m.WeightGrams, 999_999.99m, 2, "material weight");
            Number(m.DryTimeHours, 9999.9m, 1, "material drying time");
            Date(m.CreatedAt, "material creation date"); Date(m.UpdatedAt, "material update date");
        }
        foreach (var o in owners.Values)
        {
            Project(o.ProjectId); Text(o.Name, 100, "owner name", true);
        }
        if (owners.Values.GroupBy(o => (o.ProjectId, o.Name.Trim().ToUpperInvariant())).Any(g => g.Count() > 1))
            throw new BackupValidationException("The backup contains duplicate owner names in a project.");
        long imageBytes = 0;
        foreach (var p in printers.Values)
        {
            Project(p.ProjectId); Text(p.Name, 200, "printer name", true); Text(p.Notes, 2000, "printer notes");
            Text(p.RfidTagUid, 256, "printer tag"); Image(p.ImageData, p.ImageContentType);
            Date(p.CreatedAt, "printer creation date"); Date(p.UpdatedAt, "printer update date");
        }
        foreach (var l in locations.Values)
        {
            Project(l.ProjectId); Text(l.Name, 200, "storage location name", true); Text(l.Description, 2000, "storage description");
            Text(l.RfidTagUid, 256, "storage tag"); Image(l.ImageData, l.ImageContentType);
            Date(l.CreatedAt, "storage creation date"); Date(l.UpdatedAt, "storage update date");
        }
        foreach (var dryer in dryers.Values)
        {
            Project(dryer.ProjectId); Text(dryer.Name, 200, "dryer name", true); Text(dryer.Description, 2000, "dryer description");
            Text(dryer.RfidTagUid, 256, "dryer tag"); Image(dryer.ImageData, dryer.ImageContentType);
            Date(dryer.CreatedAt, "dryer creation date"); Date(dryer.UpdatedAt, "dryer update date");
            Date(dryer.DryingStartedAt, "drying start date"); Date(dryer.DryingFinishAt, "drying finish date");
            Date(dryer.DryingNotifiedAt, "drying notification date");
        }
        foreach (var s in spools.Values)
        {
            Project(s.ProjectId);
            if (!materials.TryGetValue(s.FilamentMaterialId, out var material) ||
                (material.ProjectId.HasValue && material.ProjectId != s.ProjectId))
                throw new BackupValidationException("A spool references a missing material or one from another project.");
            Related(s.OwnerId, s.ProjectId, owners, x => x.ProjectId, "owner");
            Related(s.PrinterId, s.ProjectId, printers, x => x.ProjectId, "printer");
            Related(s.StorageLocationId, s.ProjectId, locations, x => x.ProjectId, "storage location");
            Related(s.DryerId, s.ProjectId, dryers, x => x.ProjectId, "dryer");
            Text(s.RfidTagUid, 256, "spool tag"); Text(s.Notes, 2000, "spool notes"); Text(s.ReorderUrl, 2000, "spool reorder URL");
            Number(s.RemainingWeightGrams, 999_999.99m, 2, "spool remaining weight");
            Number(s.RemainingPercent, 999.99m, 2, "spool remaining percentage");
            Number(s.PurchasePrice, 99_999_999.99m, 2, "spool purchase price");
            Date(s.CreatedAt, "spool creation date"); Date(s.UpdatedAt, "spool update date");
            Date(s.OpenedAt, "spool opened date"); Date(s.RepackagedAt, "spool repackaged date");
            Date(s.ReopenedAt, "spool reopened date"); Date(s.DriedAt, "spool dried date");
            Date(s.ConsumedAt, "spool consumed date"); Date(s.PurchasedAt, "spool purchased date");
            Date(s.LowSpoolNotifiedAt, "spool notification date");
        }
        foreach (var f in favorites.Values)
        {
            if (!materials.ContainsKey(f.MaterialId))
                throw new BackupValidationException("A favorite references a missing material.");
            Date(f.CreatedAt, "favorite creation date");
        }

        void Project(Guid id)
        {
            if (!projects.ContainsKey(id)) throw new BackupValidationException("An inventory record references a missing project.");
        }
        void Image(byte[]? data, string? contentType)
        {
            Text(contentType, 100, "image content type");
            if (data == null || data.Length == 0) return;
            if (data.Length > 4 * 1024 * 1024)
                throw new BackupValidationException("An inventory image exceeds the 4 MiB limit.");
            if (contentType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif"))
                throw new BackupValidationException("An inventory image has an unsupported content type.");
            imageBytes += data.Length;
            if (imageBytes > 40 * 1024 * 1024)
                throw new BackupValidationException("The backup exceeds the 40 MiB image data limit.");
        }
    }

    private static Dictionary<Guid, T> Index<T>(List<T>? rows, Func<T, Guid> key, string label) where T : class
    {
        if (rows == null || rows.Count > 100_000)
            throw new BackupValidationException($"The backup has an invalid {label} collection.");
        var result = new Dictionary<Guid, T>();
        foreach (var row in rows)
            if (row == null || key(row) == Guid.Empty || !result.TryAdd(key(row), row))
                throw new BackupValidationException($"The backup has a null record, empty ID or duplicate ID in {label}.");
        return result;
    }

    private static void Related<T>(Guid? id, Guid projectId, Dictionary<Guid, T> rows, Func<T, Guid> project, string label)
    {
        if (id.HasValue && (!rows.TryGetValue(id.Value, out var row) || project(row) != projectId))
            throw new BackupValidationException($"A spool references a missing {label} or one from another project.");
    }

    private static void Text(string? value, int max, string label, bool required = false)
    {
        if ((required && value == null) || value?.Length > max)
            throw new BackupValidationException($"Invalid {label}; maximum length is {max} characters.");
    }

    private static void Date(DateTime value, string label)
    {
        if (value.Year < 1000) throw new BackupValidationException($"Invalid {label}.");
    }
    private static void Date(DateTime? value, string label)
    {
        if (value.HasValue) Date(value.Value, label);
    }
    private static void Number(decimal? value, decimal max, int scale, string label)
    {
        if (value.HasValue && (value < -max || value > max || decimal.Round(value.Value, scale) != value))
            throw new BackupValidationException($"Invalid {label}; value is outside its supported range or precision.");
    }

    private static BackupSpoolDto ToBackupSpool(Spool s) => new()
    {
        Id = s.Id, ProjectId = s.ProjectId, FilamentMaterialId = s.FilamentMaterialId,
        RfidTagUid = s.RfidTagUid, OpenedAt = s.OpenedAt, RepackagedAt = s.RepackagedAt,
        ReopenedAt = s.ReopenedAt, DriedAt = s.DriedAt, ConsumedAt = s.ConsumedAt,
        RemainingWeightGrams = s.RemainingWeightGrams, RemainingPercent = s.RemainingPercent,
        PrinterId = s.PrinterId, StorageLocationId = s.StorageLocationId, DryerId = s.DryerId,
        PurchasedAt = s.PurchasedAt, PurchasePrice = s.PurchasePrice, ReorderUrl = s.ReorderUrl,
        OwnerId = s.OwnerId, Notes = s.Notes, LowSpoolNotifiedAt = s.LowSpoolNotifiedAt,
        CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt
    };

    private static Spool FromBackupSpool(BackupSpoolDto s) => new()
    {
        Id = s.Id, ProjectId = s.ProjectId, FilamentMaterialId = s.FilamentMaterialId,
        RfidTagUid = s.RfidTagUid, OpenedAt = s.OpenedAt, RepackagedAt = s.RepackagedAt,
        ReopenedAt = s.ReopenedAt, DriedAt = s.DriedAt, ConsumedAt = s.ConsumedAt,
        RemainingWeightGrams = s.RemainingWeightGrams, RemainingPercent = s.RemainingPercent,
        PrinterId = s.PrinterId, StorageLocationId = s.StorageLocationId, DryerId = s.DryerId,
        PurchasedAt = s.PurchasedAt, PurchasePrice = s.PurchasePrice, ReorderUrl = s.ReorderUrl,
        OwnerId = s.OwnerId, Notes = s.Notes, LowSpoolNotifiedAt = s.LowSpoolNotifiedAt,
        CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt
    };
}
