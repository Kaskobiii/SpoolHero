using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpoolManager.Infrastructure.Repositories;
using SpoolManager.Infrastructure.Data;
using SpoolManager.Infrastructure.Services;
using SpoolManager.Server.Filters;
using SpoolManager.Shared.DTOs.Spools;
using SpoolManager.Shared.Models;

namespace SpoolManager.Server.Controllers;

[ApiController]
[Route("api/spools")]
[Authorize]
[ServiceFilter(typeof(ProjectAuthFilter))]
public class SpoolsController : ControllerBase
{
    private readonly ISpoolRepository _spools;
    private readonly IAuditService _audit;
    private readonly IOwnerRepository _owners;
    private readonly IMaterialRepository _materials;
    private readonly IPrinterRepository _printers;
    private readonly IStorageLocationRepository _storage;
    private readonly IDryerRepository _dryers;
    private readonly SpoolManagerDb _db;
    private readonly SpoolmanService _spoolman;
    private ProjectMember ProjectMember => (ProjectMember)HttpContext.Items["ProjectMember"]!;
    private string? UserName => User.FindFirst(ClaimTypes.Name)?.Value;
    private Guid UserId => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    public SpoolsController(ISpoolRepository spools, IAuditService audit, IOwnerRepository owners, IMaterialRepository materials, IPrinterRepository printers, IStorageLocationRepository storage, IDryerRepository dryers, SpoolManagerDb db, SpoolmanService spoolman)
    {
        _spools = spools;
        _owners = owners;
        _audit = audit;
        _materials = materials;
        _printers = printers;
        _storage = storage;
        _dryers = dryers;
        _db = db;
        _spoolman = spoolman;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? materialId,
        [FromQuery] Guid? printerId,
        [FromQuery] Guid? storageId,
        [FromQuery] Guid? dryerId,
        [FromQuery] bool? consumed,
        [FromQuery] string? search)
    {
        var spools = await _spools.GetAllAsync(ProjectMember.ProjectId, materialId, printerId, storageId, dryerId, consumed, search);
        return Ok(spools.Select(MapToDto));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var spool = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        return spool == null ? NotFound() : Ok(MapToDto(spool));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateSpoolRequest request)
    {
        if (request.OwnerId.HasValue && await _owners.GetByIdAsync(request.OwnerId.Value, ProjectMember.ProjectId) == null)
            return BadRequest(new { message = "The selected owner does not belong to this project." });
        var material = await _materials.GetByIdAsync(request.FilamentMaterialId);
        var error = await ValidateAsync(material, request.RemainingWeightGrams, request.InitialWeightGrams,
            request.EmptySpoolWeightGrams, request.PrinterId, request.StorageLocationId, request.DryerId);
        if (error != null) return BadRequest(new { message = error });
        var initialWeight = request.InitialWeightGrams ?? material!.WeightGrams ?? request.RemainingWeightGrams;
        var spool = new Spool
        {
            ProjectId = ProjectMember.ProjectId,
            FilamentMaterialId = request.FilamentMaterialId,
            RfidTagUid = request.RfidTagUid,
            RemainingWeightGrams = request.RemainingWeightGrams,
            InitialWeightGrams = initialWeight,
            EmptySpoolWeightGrams = request.EmptySpoolWeightGrams,
            RemainingPercent = Percent(request.RemainingWeightGrams, initialWeight),
            PrinterId = request.PrinterId,
            StorageLocationId = request.StorageLocationId,
            DryerId = request.DryerId,
            PurchasedAt = request.PurchasedAt,
            PurchasePrice = request.PurchasePrice,
            ReorderUrl = request.ReorderUrl,
            OwnerId = request.OwnerId,
            Notes = request.Notes
        };

        await using var transaction = await _db.BeginTransactionAsync();
        await _spoolman.LockProjectAsync(ProjectMember.ProjectId);
        var id = await _spools.CreateAsync(spool);
        await _spoolman.ReconcilePrimaryUidAsync(ProjectMember.ProjectId, id, null, spool.RfidTagUid);
        await transaction.CommitAsync();
        var created = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        return CreatedAtAction(nameof(GetById), new { id }, MapToDto(created!));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, UpdateSpoolRequest request)
    {
        var spool = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        if (spool == null)
            return NotFound();
        var expectedUpdatedAt = spool.UpdatedAt;

        if (request.OwnerId.HasValue && await _owners.GetByIdAsync(request.OwnerId.Value, ProjectMember.ProjectId) == null)
            return BadRequest(new { message = "The selected owner does not belong to this project." });

        if (request.ExpectedUpdatedAt.HasValue && request.ExpectedUpdatedAt.Value != expectedUpdatedAt)
            return StaleEdit();
        var material = await _materials.GetByIdAsync(request.FilamentMaterialId);
        var error = await ValidateAsync(material, request.RemainingWeightGrams, request.InitialWeightGrams,
            request.EmptySpoolWeightGrams, request.PrinterId, request.StorageLocationId, request.DryerId);
        if (error != null) return BadRequest(new { message = error });

        var oldUid = spool.RfidTagUid;
        spool.OwnerId = request.OwnerId;
        spool.FilamentMaterialId = request.FilamentMaterialId;
        spool.RemainingWeightGrams = request.RemainingWeightGrams;
        spool.InitialWeightGrams = request.InitialWeightGrams ?? spool.InitialWeightGrams ?? material!.WeightGrams ?? request.RemainingWeightGrams;
        spool.EmptySpoolWeightGrams = request.EmptySpoolWeightGrams;
        spool.RemainingPercent = Percent(request.RemainingWeightGrams, spool.InitialWeightGrams);
        spool.RfidTagUid = request.RfidTagUid;
        spool.PrinterId = request.PrinterId;
        spool.StorageLocationId = request.StorageLocationId;
        spool.DryerId = request.DryerId;
        spool.PurchasedAt = request.PurchasedAt;
        spool.PurchasePrice = request.PurchasePrice;
        spool.ReorderUrl = request.ReorderUrl;
        spool.Notes = request.Notes;
        spool.UpdatedAt = DateTime.UtcNow;

        await using var transaction = await _db.BeginTransactionAsync();
        await _spoolman.LockProjectAsync(ProjectMember.ProjectId);
        if (!await _spools.TryUpdateAsync(spool, expectedUpdatedAt)) return StaleEdit();
        await _spoolman.ReconcilePrimaryUidAsync(ProjectMember.ProjectId, id, oldUid, spool.RfidTagUid);
        await transaction.CommitAsync();
        return Ok(MapToDto((await _spools.GetByIdAsync(id, ProjectMember.ProjectId))!));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var spool = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        if (spool == null)
            return NotFound();
        var expectedUpdatedAt = spool.UpdatedAt;

        var name = spool.FilamentMaterial != null ? $"{spool.FilamentMaterial.Brand} {spool.FilamentMaterial.Type}" : $"Spool #{id}";
        await _audit.LogAsync("spool.delete",
            userId: UserId, username: UserName,
            entityType: "spool", entityId: id.ToString(), entityName: name,
            projectId: ProjectMember.ProjectId,
            ipAddress: ClientIp);

        await _spools.DeleteAsync(id);
        return NoContent();
    }

    [HttpPut("{id}/open")]
    public async Task<IActionResult> MarkOpened(Guid id)
    {
        var spool = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        if (spool == null)
            return NotFound();
        var expectedUpdatedAt = spool.UpdatedAt;
        spool.OpenedAt = DateTime.UtcNow;
        spool.UpdatedAt = DateTime.UtcNow;
        if (!await _spools.TryUpdateAsync(spool, expectedUpdatedAt)) return StaleEdit();
        return Ok(MapToDto((await _spools.GetByIdAsync(id, ProjectMember.ProjectId))!));
    }

    [HttpPut("{id}/dry")]
    public async Task<IActionResult> MarkDried(Guid id)
    {
        var spool = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        if (spool == null)
            return NotFound();
        var expectedUpdatedAt = spool.UpdatedAt;
        spool.DriedAt = DateTime.UtcNow;
        spool.UpdatedAt = DateTime.UtcNow;
        if (!await _spools.TryUpdateAsync(spool, expectedUpdatedAt)) return StaleEdit();
        return Ok(MapToDto((await _spools.GetByIdAsync(id, ProjectMember.ProjectId))!));
    }

    [HttpPut("{id}/repackage")]
    public async Task<IActionResult> MarkRepackaged(Guid id)
    {
        var spool = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        if (spool == null)
            return NotFound();
        var expectedUpdatedAt = spool.UpdatedAt;
        spool.RepackagedAt = DateTime.UtcNow;
        spool.UpdatedAt = DateTime.UtcNow;
        if (!await _spools.TryUpdateAsync(spool, expectedUpdatedAt)) return StaleEdit();
        return Ok(MapToDto((await _spools.GetByIdAsync(id, ProjectMember.ProjectId))!));
    }

    [HttpPut("{id}/reopen")]
    public async Task<IActionResult> MarkReopened(Guid id)
    {
        var spool = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        if (spool == null)
            return NotFound();
        var expectedUpdatedAt = spool.UpdatedAt;
        spool.ReopenedAt = DateTime.UtcNow;
        spool.UpdatedAt = DateTime.UtcNow;
        if (!await _spools.TryUpdateAsync(spool, expectedUpdatedAt)) return StaleEdit();
        return Ok(MapToDto((await _spools.GetByIdAsync(id, ProjectMember.ProjectId))!));
    }

    [HttpPut("{id}/consume")]
    public async Task<IActionResult> MarkConsumed(Guid id)
    {
        var spool = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        if (spool == null)
            return NotFound();
        var expectedUpdatedAt = spool.UpdatedAt;
        spool.ConsumedAt = DateTime.UtcNow;
        spool.RemainingWeightGrams = 0;
        spool.RemainingPercent = 0;
        spool.UpdatedAt = DateTime.UtcNow;
        if (!await _spools.TryUpdateAsync(spool, expectedUpdatedAt)) return StaleEdit();

        var name = spool.FilamentMaterial != null ? $"{spool.FilamentMaterial.Brand} {spool.FilamentMaterial.Type}" : $"Spool #{id}";
        await _audit.LogAsync("spool.consume",
            userId: UserId, username: UserName,
            entityType: "spool", entityId: id.ToString(), entityName: name,
            projectId: ProjectMember.ProjectId,
            ipAddress: ClientIp);

        return Ok(MapToDto((await _spools.GetByIdAsync(id, ProjectMember.ProjectId))!));
    }

    [HttpPatch("{id}/remaining")]
    public async Task<IActionResult> UpdateRemaining(Guid id, UpdateRemainingRequest request)
    {
        var spool = await _spools.GetByIdAsync(id, ProjectMember.ProjectId);
        if (spool == null)
            return NotFound();
        var expectedUpdatedAt = spool.UpdatedAt;
        if (request.ExpectedUpdatedAt.HasValue && request.ExpectedUpdatedAt.Value != expectedUpdatedAt)
            return StaleEdit();
        if (!WeightIsValid(request.RemainingWeightGrams))
            return BadRequest(new { message = "Remaining filament must be between 0 and 999999.99 grams, with at most two decimal places." });
        spool.RemainingWeightGrams = request.RemainingWeightGrams;
        spool.RemainingPercent = Percent(request.RemainingWeightGrams, spool.InitialWeightGrams ?? spool.FilamentMaterial?.WeightGrams);
        spool.LowSpoolNotifiedAt = null;
        spool.UpdatedAt = DateTime.UtcNow;
        if (!await _spools.TryUpdateAsync(spool, expectedUpdatedAt)) return StaleEdit();
        return Ok(MapToDto((await _spools.GetByIdAsync(id, ProjectMember.ProjectId))!));
    }

    private IActionResult StaleEdit() => Conflict(new { message = "This spool changed while you were editing. Reload it and try again." });

    private static bool WeightIsValid(decimal? weight) => !weight.HasValue ||
        (weight.Value >= 0 && weight.Value <= 999999.99m && decimal.Round(weight.Value, 2) == weight.Value);

    private static decimal Percent(decimal grams, decimal? initial) => initial > 0
        ? Math.Clamp(Math.Round(grams / initial.Value * 100m, 2), 0m, 100m)
        : grams > 0 ? 100m : 0m;

    private async Task<string?> ValidateAsync(FilamentMaterial? material, decimal remaining, decimal? initial,
        decimal? empty, Guid? printer, Guid? storage, Guid? dryer)
    {
        if (material == null || (material.ProjectId.HasValue && material.ProjectId != ProjectMember.ProjectId))
            return "The selected material does not belong to this project.";
        if (!WeightIsValid(remaining) || !WeightIsValid(initial) || !WeightIsValid(empty))
            return "Weights must be between 0 and 999999.99 grams, with at most two decimal places.";
        if ((printer.HasValue ? 1 : 0) + (storage.HasValue ? 1 : 0) + (dryer.HasValue ? 1 : 0) > 1)
            return "Select one location for the spool.";
        if (printer.HasValue && (await _printers.GetByIdAsync(printer.Value))?.ProjectId != ProjectMember.ProjectId)
            return "The selected printer does not belong to this project.";
        if (storage.HasValue && (await _storage.GetByIdAsync(storage.Value))?.ProjectId != ProjectMember.ProjectId)
            return "The selected storage does not belong to this project.";
        if (dryer.HasValue && (await _dryers.GetByIdAsync(dryer.Value))?.ProjectId != ProjectMember.ProjectId)
            return "The selected dryer does not belong to this project.";
        return null;
    }

    private static SpoolDto MapToDto(Spool s) => new()
    {
        Id = s.Id,
        ProjectId = s.ProjectId,
        FilamentMaterialId = s.FilamentMaterialId,
        MaterialType = s.FilamentMaterial?.Type ?? string.Empty,
        MaterialBrand = s.FilamentMaterial?.Brand ?? string.Empty,
        MaterialColorHex = s.FilamentMaterial?.ColorHex ?? "FFFFFF",
        MaterialColorName = s.FilamentMaterial?.ColorName,
        RfidTagUid = s.RfidTagUid,
        OpenedAt = s.OpenedAt,
        RepackagedAt = s.RepackagedAt,
        ReopenedAt = s.ReopenedAt,
        DriedAt = s.DriedAt,
        ConsumedAt = s.ConsumedAt,
        RemainingWeightGrams = s.RemainingWeightGrams,
        InitialWeightGrams = s.InitialWeightGrams,
        EmptySpoolWeightGrams = s.EmptySpoolWeightGrams,
        RemainingPercent = Percent(s.RemainingWeightGrams, s.InitialWeightGrams ?? s.FilamentMaterial?.WeightGrams),
        PrinterId = s.PrinterId,
        PrinterName = s.Printer?.Name,
        StorageLocationId = s.StorageLocationId,
        StorageLocationName = s.StorageLocation?.Name,
        DryerId = s.DryerId,
        DryerName = s.Dryer?.Name,
        MaterialDryTimeHours = s.FilamentMaterial?.DryTimeHours,
        PurchasedAt = s.PurchasedAt,
        PurchasePrice = s.PurchasePrice,
        ReorderUrl = s.ReorderUrl,
        OwnerId = s.OwnerId,
        OwnerName = s.Owner?.Name,
        Notes = s.Notes,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt
    };
}
