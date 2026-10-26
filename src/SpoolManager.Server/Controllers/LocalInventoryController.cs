using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpoolManager.Infrastructure.Data;
using SpoolManager.Server.Services;
using SpoolManager.Shared.DTOs.Admin;
using SpoolManager.Shared.DTOs.Auth;

namespace SpoolManager.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/local")]
public class LocalInventoryController(SpoolManagerDb db, LocalInventoryIdentity inventory) : ControllerBase
{
    [HttpGet("session")]
    public async Task<IActionResult> Session()
    {
        Response.Headers.CacheControl = "no-store";
        var user = await db.Users.FirstAsync(u => u.Id == inventory.Id);
        return Ok(new UserDto { Id = inventory.Id, Username = "Shared inventory", IsPlatformAdmin = true, IsSuperAdmin = true,
            DashboardOnboardingDismissed = user.DashboardOnboardingDismissed, CreatedAt = user.CreatedAt });
    }
    [HttpPut("dashboard-onboarding")]
    public async Task<IActionResult> Onboarding(SetDashboardOnboardingRequest request)
    {
        await db.Users.Where(u => u.Id == inventory.Id).Set(u => u.DashboardOnboardingDismissed, request.Dismissed).UpdateAsync();
        return NoContent();
    }
    [HttpGet("notifications")]
    public async Task<IActionResult> Notifications()
    {
        var user = await db.Users.FirstAsync(u => u.Id == inventory.Id);
        return Ok(new NotificationPrefsDto { NotifySpoolLow = user.NotifySpoolLow, NotifyDryerDone = user.NotifyDryerDone, NotifyTicketReply = false });
    }
    [HttpPut("notifications")]
    public async Task<IActionResult> Notifications(NotificationPrefsDto request)
    {
        await db.Users.Where(u => u.Id == inventory.Id).Set(u => u.NotifySpoolLow, request.NotifySpoolLow)
            .Set(u => u.NotifyDryerDone, request.NotifyDryerDone).UpdateAsync();
        return NoContent();
    }
    [HttpPut("language")]
    public async Task<IActionResult> Language(SetLanguageRequest request)
    {
        var language = request.Language?.ToLowerInvariant();
        if (language is not ("en" or "de")) return BadRequest();
        await db.Users.Where(u => u.Id == inventory.Id).Set(u => u.PreferredLanguage, language).UpdateAsync();
        return NoContent();
    }
}
