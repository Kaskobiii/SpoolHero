using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpoolManager.Infrastructure.Repositories;
using SpoolManager.Infrastructure.Services;
using SpoolManager.Shared.DTOs.Projects;
using SpoolManager.Shared.Models;

namespace SpoolManager.Server.Controllers;

[ApiController]
[Route("api/projects")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectRepository _projects;
    private readonly IProjectMemberRepository _members;
    private readonly IInvitationRepository _invitations;
    private readonly IAuditService _audit;

    public ProjectsController(IProjectRepository projects, IProjectMemberRepository members, IInvitationRepository invitations, IAuditService audit)
    {
        _projects = projects;
        _members = members;
        _invitations = invitations;
        _audit = audit;
    }

    private Guid UserId => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private string? UserName => User.FindFirst(ClaimTypes.Name)?.Value;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet]
    public async Task<IActionResult> GetMyProjects()
    {
        var projects = await _projects.GetForUserAsync(UserId);
        var memberEntries = new List<ProjectMember>();
        foreach (var p in projects)
            memberEntries.Add((await _members.GetAsync(p.Id, UserId))!);

        var dtos = projects.Select((p, i) => new ProjectDto
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            MyRole = memberEntries.FirstOrDefault(m => m.ProjectId == p.Id)?.Role ?? "member",
            CreatedAt = p.CreatedAt
        }).ToList();

        return Ok(dtos);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });
        if (request.Name.Length > 200)
            return BadRequest(new { message = "Name must not exceed 200 characters." });

        var project = new Project
        {
            Name = request.Name,
            Description = request.Description
        };
        var id = await _projects.CreateAsync(project);

        var member = new ProjectMember
        {
            ProjectId = id,
            UserId = UserId,
            Role = "admin"
        };
        await _members.AddAsync(member);

        await _audit.LogAsync("project.create",
            userId: UserId, username: UserName,
            entityType: "project", entityId: id.ToString(), entityName: request.Name,
            projectId: id, projectName: request.Name,
            ipAddress: ClientIp);

        var created = await _projects.GetByIdAsync(id);
        return CreatedAtAction(nameof(GetById), new { id }, new ProjectDto
        {
            Id = created!.Id,
            Name = created.Name,
            Description = created.Description,
            MyRole = "admin",
            CreatedAt = created.CreatedAt
        });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!await _members.IsMemberAsync(id, UserId))
            return Forbid();
        var project = await _projects.GetByIdAsync(id);
        if (project == null)
            return NotFound();
        var member = await _members.GetAsync(id, UserId);
        return Ok(new ProjectDto { Id = project.Id, Name = project.Name, Description = project.Description, MyRole = member?.Role ?? "member", CreatedAt = project.CreatedAt });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, UpdateProjectRequest request)
    {
        var member = await _members.GetAsync(id, UserId);
        if (member == null)
            return Forbid();
        if (member.Role != "admin")
            return StatusCode(403, new { message = "Only admins can update the project." });

        var project = await _projects.GetByIdAsync(id);
        if (project == null)
            return NotFound();
        project.Name = request.Name;
        project.Description = request.Description;
        await _projects.UpdateAsync(project);
        return Ok(new ProjectDto { Id = project.Id, Name = project.Name, Description = project.Description, MyRole = "admin", CreatedAt = project.CreatedAt });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var member = await _members.GetAsync(id, UserId);
        if (member == null)
            return Forbid();
        if (member.Role != "admin")
            return StatusCode(403, new { message = "Only admins can delete the project." });

        var project = await _projects.GetByIdAsync(id);
        if (project == null)
            return NotFound();

        await _audit.LogAsync("project.delete",
            userId: UserId, username: UserName,
            entityType: "project", entityId: id.ToString(), entityName: project.Name,
            projectId: id, projectName: project.Name,
            ipAddress: ClientIp);

        await _projects.DeleteAsync(id);
        return NoContent();
    }

}
