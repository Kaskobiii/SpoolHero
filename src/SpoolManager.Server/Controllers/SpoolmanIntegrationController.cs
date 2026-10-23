using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpoolManager.Infrastructure.Services;
using SpoolManager.Server.Filters;
using SpoolManager.Server.Services;
using SpoolManager.Shared.Models;

namespace SpoolManager.Server.Controllers;

[ApiController]
[Route("api/integrations/spoolman")]
[Authorize]
[ServiceFilter(typeof(ProjectAuthFilter))]
public class SpoolmanIntegrationController(SpoolmanService spoolman, SpoolmanConnections connections) : ControllerBase
{
    private ProjectMember Member => (ProjectMember)HttpContext.Items["ProjectMember"]!;
    [HttpGet]
    public async Task<IActionResult> Status()
    {
        if (Member.Role != "admin") return Forbid();
        Response.Headers.CacheControl = "no-store";
        var status = await spoolman.StatusAsync(Member.ProjectId);
        status.ConnectedPrinters = connections.Count(Member.ProjectId);
        return Ok(status);
    }
    [HttpPost("enable")]
    [HttpPost("rotate")]
    public async Task<IActionResult> Enable()
    {
        if (Member.Role != "admin") return Forbid();
        Response.Headers.CacheControl = "no-store";
        return Ok(await spoolman.EnableAsync(Member.ProjectId));
    }
    [HttpDelete]
    public async Task<IActionResult> Disable()
    {
        if (Member.Role != "admin") return Forbid();
        await spoolman.DisableAsync(Member.ProjectId);
        return NoContent();
    }
}
