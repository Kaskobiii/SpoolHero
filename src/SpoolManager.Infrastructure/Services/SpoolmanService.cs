using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Data;
using SpoolManager.Infrastructure.Data;
using SpoolManager.Shared.DTOs.Integrations;
using SpoolManager.Shared.Models;

namespace SpoolManager.Infrastructure.Services;

public class SpoolmanValidationException(string message) : Exception(message) { }

public class SpoolmanService(SpoolManagerDb db)
{
    public async Task<Guid?> AuthenticateAsync(string token, bool touch = true)
    {
        if (!Regex.IsMatch(token, "^[A-Fa-f0-9]{64}$")) return null;
        var hash = Hash(token);
        var record = await db.SpoolmanIntegrations.FirstOrDefaultAsync(x => x.TokenHash == hash);
        if (record == null) return null;
        if (touch && (!record.LastSeenAt.HasValue || record.LastSeenAt < DateTime.UtcNow.AddSeconds(-30)))
            await db.SpoolmanIntegrations.Where(x => x.ProjectId == record.ProjectId && x.TokenHash == hash)
                .Set(x => x.LastSeenAt, DateTime.UtcNow).UpdateAsync();
        return record.ProjectId;
    }

    public async Task<SpoolmanConnectionDto> EnableAsync(Guid projectId)
    {
        await EnsureIntegrationAsync(projectId);
        await using var tx = await db.BeginTransactionAsync();
        await LockAsync(projectId);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTime.UtcNow;
        await db.SpoolmanIntegrations.Where(x => x.ProjectId == projectId)
            .Set(x => x.TokenHash, Hash(token)).Set(x => x.TokenCreatedAt, now).UpdateAsync();
        await EnsureMapsLockedAsync(projectId);
        await tx.CommitAsync();
        return new() { ConnectionPath = $"/api/spoolman/{token}", TokenCreatedAt = now };
    }

    public Task DisableAsync(Guid projectId) => db.SpoolmanIntegrations.Where(x => x.ProjectId == projectId)
        .Set(x => x.TokenHash, (string?)null).UpdateAsync();

    public async Task<SpoolmanIntegrationStatusDto> StatusAsync(Guid projectId)
    {
        var integration = await db.SpoolmanIntegrations.FirstOrDefaultAsync(x => x.ProjectId == projectId);
        var rows = integration == null ? [] : await GetRowsAsync(projectId, true);
        return new()
        {
            Enabled = integration?.TokenHash != null, TokenCreatedAt = integration?.TokenCreatedAt,
            LastSeenAt = integration?.LastSeenAt, LastUsageAt = integration?.LastUsageAt,
            Spools = rows.Select(x => new SpoolmanSpoolStatusDto
            {
                Id = x.Map.Id, SpoolId = x.Spool.Id, CardUids = AllUids(x.Map, x.Spool),
                RemainingWeightGrams = x.Spool.RemainingWeightGrams,
                MissingDensity = x.Material.DensityGCm3 is null or <= 0,
                MissingDiameter = x.Material.DiameterMm <= 0,
                CanTrackUsage = x.Material.DensityGCm3 > 0 && x.Material.DiameterMm > 0,
                TrackingIssue = x.Material.DensityGCm3 is null or <= 0 ? "Set the material density to enable usage tracking." :
                    x.Material.DiameterMm <= 0 ? "Set the material diameter to enable usage tracking." : null,
                MaterialName = $"{x.Material.Brand} {x.Material.Type} {x.Material.ColorName}".Trim()
            }).ToList()
        };
    }

    private async Task EnsureIntegrationAsync(Guid projectId) => await db.ExecuteAsync(
        "INSERT IGNORE INTO spoolman_integrations(project_id,next_spool_id) VALUES(@id,1)",
        new DataParameter("id", projectId.ToString()));
    public Task LockProjectAsync(Guid projectId) => LockAsync(projectId);

    private async Task LockAsync(Guid projectId) => await db.ExecuteAsync(
        "UPDATE spoolman_integrations SET next_spool_id=next_spool_id WHERE project_id=@id",
        new DataParameter("id", projectId.ToString()));

    private async Task EnsureMapsLockedAsync(Guid projectId)
    {
        var integration = await db.SpoolmanIntegrations.FirstAsync(x => x.ProjectId == projectId);
        var mapped = await db.SpoolmanSpools.Where(x => x.ProjectId == projectId).Select(x => x.SpoolId).ToListAsync();
        var missing = await db.Spools.Where(x => x.ProjectId == projectId && !mapped.Contains(x.Id)).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id).ToListAsync();
        foreach (var spoolId in missing)
        {
            if (integration.NextSpoolId == int.MaxValue) throw new SpoolmanValidationException("The Spoolman ID range is exhausted.");
            await db.InsertAsync(new SpoolmanSpoolMap { ProjectId = projectId, Id = integration.NextSpoolId++, SpoolId = spoolId });
        }
        if (missing.Count > 0)
            await db.SpoolmanIntegrations.Where(x => x.ProjectId == projectId).Set(x => x.NextSpoolId, integration.NextSpoolId).UpdateAsync();
    }

    public sealed record Row(SpoolmanSpoolMap Map, Spool Spool, FilamentMaterial Material);
    public async Task<List<Row>> GetRowsAsync(Guid projectId, bool includeArchived)
    {
        await using (var tx = await db.BeginTransactionAsync())
        {
            await LockAsync(projectId);
            await EnsureMapsLockedAsync(projectId);
            await tx.CommitAsync();
        }
        return await (from map in db.SpoolmanSpools
            join spool in db.Spools on map.SpoolId equals spool.Id
            join material in db.FilamentMaterials on spool.FilamentMaterialId equals material.Id
            where map.ProjectId == projectId && spool.ProjectId == projectId && (includeArchived || spool.ConsumedAt == null)
            orderby map.Id
            select new Row(map, spool, material)).ToListAsync();
    }

    public async Task<Row?> GetRowAsync(Guid projectId, int id) => await (from map in db.SpoolmanSpools
        join spool in db.Spools on map.SpoolId equals spool.Id
        join material in db.FilamentMaterials on spool.FilamentMaterialId equals material.Id
        where map.ProjectId == projectId && map.Id == id && spool.ProjectId == projectId
        select new Row(map, spool, material)).FirstOrDefaultAsync();

    public static string[] ParseUids(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var decoded = value.Trim();
        if (decoded.StartsWith('"'))
        {
            try { decoded = JsonSerializer.Deserialize<string>(decoded) ?? ""; }
            catch (JsonException) { throw new SpoolmanValidationException("card_uids must contain a JSON-encoded comma-separated string."); }
        }
        if (decoded.Length > 0 && decoded.Split(',').Any(string.IsNullOrWhiteSpace))
            throw new SpoolmanValidationException("card_uids contains an empty tag identifier.");
        var uids = decoded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant()).Distinct().ToArray();
        if (uids.Length > 64 || string.Join(',', uids).Length > 2048 || uids.Any(x => !Regex.IsMatch(x, "^(?:[0-9A-F]{2}){4,16}$")))
            throw new SpoolmanValidationException("NFC UIDs must contain 4–16 bytes of hexadecimal characters, with at most 64 tags per spool.");
        return uids;
    }

    public static string[] AllUids(SpoolmanSpoolMap map, Spool spool)
    {
        var uids = ParseUids(map.CardUids).ToList();
        try { uids.AddRange(ParseUids(spool.RfidTagUid)); }
        catch (SpoolmanValidationException) { /* Older non-UID identifiers remain in SpoolHero only. */ }
        return uids.Distinct().ToArray();
    }

    public async Task<Row?> SetCardUidsAsync(Guid projectId, int id, string value)
    {
        var uids = ParseUids(value);
        await using var tx = await db.BeginTransactionAsync();
        await LockAsync(projectId);
        var target = await GetRowAsync(projectId, id);
        if (target == null) return null;
        await SetCardUidsLockedAsync(projectId, target, uids);
        await tx.CommitAsync();
        return await GetRowAsync(projectId, id);
    }

    // Called inside the editor's existing transaction after LockProjectAsync,
    // so manual UID changes and printer-side linking use the same lock order.
    public async Task ReconcilePrimaryUidAsync(Guid projectId, Guid spoolId, string? oldUid, string? newUid)
    {
        if (!await db.SpoolmanIntegrations.AnyAsync(x => x.ProjectId == projectId)) return;
        await EnsureMapsLockedAsync(projectId);
        if (oldUid == newUid) return;
        var map = await db.SpoolmanSpools.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.SpoolId == spoolId);
        if (map == null) return;
        var row = await GetRowAsync(projectId, map.Id);
        if (row == null) return;
        string[] oldUids, newUids;
        try { oldUids = ParseUids(oldUid); } catch (SpoolmanValidationException) { oldUids = []; }
        try { newUids = ParseUids(newUid); } catch (SpoolmanValidationException) { newUids = []; }
        var uids = ParseUids(map.CardUids).Except(oldUids).Concat(newUids).Distinct().ToArray();
        await SetCardUidsLockedAsync(projectId, row, uids, false);
    }

    private async Task SetCardUidsLockedAsync(Guid projectId, Row target, string[] uids, bool updatePrimary = true)
    {
        // Atomic reassignment prevents two printer clients leaving a UID on two spools.
        var otherMaps = await db.SpoolmanSpools.Where(x => x.ProjectId == projectId && x.Id != target.Map.Id).ToListAsync();
        foreach (var other in otherMaps)
        {
            var retained = ParseUids(other.CardUids).Except(uids).ToArray();
            if (string.Join(',', retained) != other.CardUids)
                await db.SpoolmanSpools.Where(x => x.ProjectId == projectId && x.Id == other.Id).Set(x => x.CardUids, string.Join(',', retained)).UpdateAsync();
        }
        var otherSpools = await db.Spools.Where(x => x.ProjectId == projectId && x.Id != target.Spool.Id && x.RfidTagUid != null).ToListAsync();
        foreach (var other in otherSpools)
        {
            string[] old;
            try { old = ParseUids(other.RfidTagUid); } catch (SpoolmanValidationException) { continue; }
            if (old.Any(x => uids.Contains(x)))
                await db.Spools.Where(x => x.Id == other.Id && x.ProjectId == projectId).Set(x => x.RfidTagUid, (string?)null).Set(x => x.UpdatedAt, DateTime.UtcNow).UpdateAsync();
        }
        await db.SpoolmanSpools.Where(x => x.ProjectId == projectId && x.Id == target.Map.Id).Set(x => x.CardUids, string.Join(',', uids)).UpdateAsync();
        if (updatePrimary)
            await db.Spools.Where(x => x.Id == target.Spool.Id && x.ProjectId == projectId)
                .Set(x => x.RfidTagUid, uids.FirstOrDefault()).Set(x => x.UpdatedAt, DateTime.UtcNow).UpdateAsync();
    }

    public async Task<Row?> UseAsync(Guid projectId, int id, decimal? length, decimal? weight)
    {
        if (length.HasValue == weight.HasValue || length is <= 0 or > 1_000_000_000m || weight is <= 0 or > 1_000_000m)
            throw new SpoolmanValidationException("Supply exactly one positive use_length (mm) or use_weight (g) within the supported range.");
        await using var tx = await db.BeginTransactionAsync();
        await LockAsync(projectId);
        var row = await GetRowAsync(projectId, id);
        if (row == null) return null;
        if (length.HasValue && (row.Material.DensityGCm3 is null or <= 0 || row.Material.DiameterMm <= 0))
            throw new SpoolmanValidationException("Set a positive material density and diameter in SpoolHero before tracking filament length.");
        var grams = weight ?? length!.Value * (decimal)Math.PI * row.Material.DiameterMm * row.Material.DiameterMm / 4m * row.Material.DensityGCm3!.Value / 1000m;
        var accumulated = grams + row.Map.UsageRemainderGrams;
        var deduction = decimal.Floor(accumulated * 100m) / 100m;
        var remainder = accumulated - deduction;
        var now = DateTime.UtcNow;
        // The subtraction happens inside the database, preserving concurrent deductions.
        await db.Spools.Where(x => x.Id == row.Spool.Id && x.ProjectId == projectId)
            .Set(x => x.RemainingWeightGrams, x => x.RemainingWeightGrams > deduction ? x.RemainingWeightGrams - deduction : 0)
            .Set(x => x.UpdatedAt, now).UpdateAsync();
        // Percentage is legacy storage only; grams are authoritative.
        if (row.Spool.InitialWeightGrams is > 0)
        {
            var nominal = row.Spool.InitialWeightGrams.Value;
            await db.Spools.Where(x => x.Id == row.Spool.Id && x.ProjectId == projectId)
                .Set(x => x.RemainingPercent, x => x.RemainingWeightGrams >= nominal ? 100m : x.RemainingWeightGrams / nominal * 100m).UpdateAsync();
        }
        await db.SpoolmanSpools.Where(x => x.ProjectId == projectId && x.Id == id)
            .Set(x => x.UsageRemainderGrams, remainder).UpdateAsync();
        await db.SpoolmanIntegrations.Where(x => x.ProjectId == projectId).Set(x => x.LastUsageAt, now).UpdateAsync();
        // Audit is in the same transaction as the weight update.
        await db.InsertAsync(new AuditLog
        {
            Id = Guid.NewGuid(), Timestamp = now, Action = "spool.printer_usage", EntityType = "spool",
            EntityId = row.Spool.Id.ToString(), ProjectId = projectId,
            Details = JsonSerializer.Serialize(new { source = "Spoolman compatibility", usedGrams = grams, deductedGrams = deduction, useLengthMm = length })
        });
        await tx.CommitAsync();
        return await GetRowAsync(projectId, id);
    }

    public static object ToWire(Row row)
    {
        var m = row.Material; var s = row.Spool;
        var nominal = s.InitialWeightGrams is > 0 ? s.InitialWeightGrams : null;
        var gramsPerMm = m.DensityGCm3 > 0 && m.DiameterMm > 0 ? (decimal)Math.PI * m.DiameterMm * m.DiameterMm / 4m * m.DensityGCm3.Value / 1000m : (decimal?)null;
        return new
        {
            id = row.Map.Id, registered = s.CreatedAt, first_used = s.OpenedAt, last_used = s.UpdatedAt,
            filament = new
            {
                id = row.Map.Id, registered = m.CreatedAt, name = m.ColorName ?? $"{m.Brand} {m.Type}",
                vendor = new { id = row.Map.Id, registered = m.CreatedAt, name = m.Brand },
                material = m.Type, price = m.PricePerKg.HasValue && nominal.HasValue ? m.PricePerKg * nominal / 1000m : null,
                density = m.DensityGCm3, diameter = m.DiameterMm, weight = nominal, spool_weight = s.EmptySpoolWeightGrams,
                color_hex = m.ColorHex.TrimStart('#'), settings_extruder_temp = (m.MinTempCelsius + m.MaxTempCelsius) / 2,
                settings_bed_temp = m.BedTempCelsius, extra = new Dictionary<string, string> { ["variant"] = JsonSerializer.Serialize("") }
            },
            initial_weight = nominal, spool_weight = s.EmptySpoolWeightGrams,
            used_weight = nominal.HasValue ? Math.Max(0, nominal.Value - s.RemainingWeightGrams) : (decimal?)null,
            remaining_weight = s.RemainingWeightGrams,
            remaining_length = gramsPerMm > 0 ? s.RemainingWeightGrams / gramsPerMm.Value : (decimal?)null,
            used_length = nominal.HasValue && gramsPerMm > 0 ? Math.Max(0, nominal.Value - s.RemainingWeightGrams) / gramsPerMm.Value : (decimal?)null,
            location = s.StorageLocationId?.ToString(), lot_nr = (string?)null, comment = s.Notes,
            archived = s.ConsumedAt != null,
            extra = new Dictionary<string, string> { ["card_uids"] = JsonSerializer.Serialize(string.Join(',', AllUids(row.Map, s))) }
        };
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
