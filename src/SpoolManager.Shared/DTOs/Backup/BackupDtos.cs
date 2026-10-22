using System.Text.Json.Serialization;
using SpoolManager.Shared.Models;

namespace SpoolManager.Shared.DTOs.Backup;

public class InventoryBackupDocument
{
    public const string CurrentFormat = "SpoolHero.InventoryBackup";
    public const int CurrentVersion = 1;
    public const long MaxFileBytes = 64 * 1024 * 1024;

    public string Format { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime ExportedAt { get; set; }
    [JsonRequired] public List<Project> Projects { get; set; } = [];
    [JsonRequired] public List<FilamentMaterial> Materials { get; set; } = [];
    [JsonRequired] public List<SpoolOwner> Owners { get; set; } = [];
    [JsonRequired] public List<BackupSpoolDto> Spools { get; set; } = [];
    [JsonRequired] public List<Printer> Printers { get; set; } = [];
    [JsonRequired] public List<StorageLocation> StorageLocations { get; set; } = [];
    [JsonRequired] public List<Dryer> Dryers { get; set; } = [];
    [JsonRequired] public List<BackupFavoriteDto> MaterialFavorites { get; set; } = [];
    public bool? OpenFilamentDbEnabled { get; set; }
}

public class BackupFavoriteDto
{
    public Guid MaterialId { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Explicit persisted fields keep navigation properties and user data out of backups.
public class BackupSpoolDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FilamentMaterialId { get; set; }
    public string? RfidTagUid { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? RepackagedAt { get; set; }
    public DateTime? ReopenedAt { get; set; }
    public DateTime? DriedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public decimal RemainingWeightGrams { get; set; }
    public decimal RemainingPercent { get; set; }
    public Guid? PrinterId { get; set; }
    public Guid? StorageLocationId { get; set; }
    public Guid? DryerId { get; set; }
    public DateTime? PurchasedAt { get; set; }
    public decimal? PurchasePrice { get; set; }
    public string? ReorderUrl { get; set; }
    public Guid? OwnerId { get; set; }
    public string? Notes { get; set; }
    public DateTime? LowSpoolNotifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class BackupImportResultDto
{
    public List<Guid> ProjectIds { get; set; } = [];
    public int ProjectCount { get; set; }
    public int MaterialCount { get; set; }
    public int OwnerCount { get; set; }
    public int SpoolCount { get; set; }
    public int PrinterCount { get; set; }
    public int StorageLocationCount { get; set; }
    public int DryerCount { get; set; }
    public int FavoriteCount { get; set; }
    public bool CatalogSettingApplied { get; set; }
}
