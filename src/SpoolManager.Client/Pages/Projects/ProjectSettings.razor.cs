using Microsoft.AspNetCore.Components;
using SpoolManager.Client.Services;
using SpoolManager.Shared.DTOs.Projects;
namespace SpoolManager.Client.Pages.Projects;
public partial class ProjectSettings
{
    [Parameter] public Guid Id { get; set; }
    [Inject] private ProjectService Project { get; set; } = default!;
    [Inject] private LocalizationService L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    private ProjectDto? _project;
    private bool _loading = true, _saving, _saved;
    private string? _error;
    protected override async Task OnInitializedAsync()
    {
        _project = (await Project.GetMyProjectsAsync())?.FirstOrDefault(p => p.Id == Id);
        if (_project == null) Nav.NavigateTo("/projects");
        _loading = false;
    }
    private async Task SaveAsync()
    {
        if (_project == null || _saving) return;
        _saving = true; _saved = false; _error = null;
        try
        {
            using var response = await Project.UpdateAsync(Id, new UpdateProjectRequest { Name = _project.Name, Description = _project.Description });
            if (!response.IsSuccessStatusCode) { _error = await BackupService.GetErrorAsync(response, L["common.error"]); return; }
            if (Project.CurrentProject?.Id == Id) await Project.SwitchProjectAsync(_project);
            _saved = true;
        }
        catch (HttpRequestException) { _error = L["common.error"]; }
        finally { _saving = false; }
    }
}
