using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SpoolManager.Infrastructure.Services;
using SpoolManager.Shared.DTOs.Backup;

namespace SpoolManager.Server.Controllers;

[ApiController]
[Route("api/backup")]
[Authorize]
public class BackupController(IBackupService backup, ILogger<BackupController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions ExportOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private Guid UserId => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private bool IsPlatformAdmin => User.FindFirst("is_platform_admin")?.Value == "true";

    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        try
        {
            var document = await backup.ExportAsync(UserId, IsPlatformAdmin);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ExportOptions);
            if (bytes.LongLength > InventoryBackupDocument.MaxFileBytes)
                return BadRequest(new { message = "This backup exceeds the supported 64 MiB file limit." });
            Response.Headers.CacheControl = "no-store";
            return File(bytes, "application/json", $"SpoolHero-backup-{document.ExportedAt:yyyy-MM-dd-HHmmss}.json");
        }
        catch (BackupValidationException error)
        {
            return BadRequest(new { message = error.Message });
        }
    }

    [HttpPost("import")]
    [RequestSizeLimit(InventoryBackupDocument.MaxFileBytes)]
    public async Task<IActionResult> Import([FromBody] InventoryBackupDocument document,
        [FromQuery] bool applyCatalogSetting = false)
    {
        // Shared catalog GUIDs are preserved. Only a platform admin may create those
        // global records or restore the installation's catalog preference.
        if (!IsPlatformAdmin)
            return Forbid();
        try
        {
            return Ok(await backup.ImportAsync(document, UserId, applyCatalogSetting));
        }
        catch (BackupValidationException error)
        {
            return BadRequest(new { message = error.Message });
        }
        catch (BackupConflictException error)
        {
            return Conflict(new { message = error.Message });
        }
        catch (MySqlException error)
        {
            logger.LogWarning(error, "Inventory backup restore rolled back for user {UserId}", UserId);
            if (error.Number is 1062 or 1213 or 1205)
                return Conflict(new { message = "An inventory ID already exists or another restore is in progress. Nothing was imported. Restore into a fresh instance or try again when the other operation has finished." });
            return BadRequest(new { message = "The inventory could not be restored. Nothing was imported. Check that the file is a complete SpoolHero inventory backup." });
        }
    }
}
