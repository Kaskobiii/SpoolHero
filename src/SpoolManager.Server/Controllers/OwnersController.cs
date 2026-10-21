using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SpoolManager.Infrastructure.Repositories;
using SpoolManager.Server.Filters;
using SpoolManager.Shared.DTOs.Owners;
using SpoolManager.Shared.Models;

namespace SpoolManager.Server.Controllers;

[ApiController]
[Route("api/owners")]
[Authorize]
[ServiceFilter(typeof(ProjectAuthFilter))]
public class OwnersController(IOwnerRepository owners) : ControllerBase
{
    private Guid ProjectId => ((ProjectMember)HttpContext.Items["ProjectMember"]!).ProjectId;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok((await owners.GetAllAsync(ProjectId)).Select(o => new OwnerDto { Id = o.Id, Name = o.Name }));

    [HttpPost]
    public async Task<IActionResult> Create(SaveOwnerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "An owner name is required." });
        var owner = new SpoolOwner { Id = Guid.NewGuid(), ProjectId = ProjectId, Name = request.Name.Trim() };
        try { await owners.CreateAsync(owner); }
        catch (MySqlException ex) when (ex.Number == 1062)
        { return Conflict(new { message = "An owner with that name already exists in this project." }); }
        return StatusCode(201, new OwnerDto { Id = owner.Id, Name = owner.Name });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, SaveOwnerRequest request)
    {
        var owner = await owners.GetByIdAsync(id, ProjectId);
        if (owner == null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "An owner name is required." });
        owner.Name = request.Name.Trim();
        try { await owners.UpdateAsync(owner); }
        catch (MySqlException ex) when (ex.Number == 1062)
        { return Conflict(new { message = "An owner with that name already exists in this project." }); }
        return Ok(new OwnerDto { Id = owner.Id, Name = owner.Name });
    }
}
