using Microsoft.AspNetCore.Components;
using SpoolManager.Client.Services;
using SpoolManager.Shared.DTOs.Projects;

namespace SpoolManager.Client.Pages.Projects;

public partial class ProjectList
{
    [Inject] private ProjectService Project { get; set; } = default!;
    [Inject] private LocalizationService L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private bool _loading = true;
    private List<ProjectDto> _projects = [];
    protected override async Task OnInitializedAsync()
    {
        _projects = await Project.GetMyProjectsAsync() ?? [];
        _loading = false;
    }

    private async Task SwitchAsync(ProjectDto p)
    {
        await Project.SwitchProjectAsync(p);
        Nav.NavigateTo("/");
    }

}
