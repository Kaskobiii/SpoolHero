using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using SpoolManager.Client.Services;
using SpoolManager.Shared.DTOs.Projects;

namespace SpoolManager.Client.Layout;

public partial class MainLayout
{
    [Inject] private AuthService Auth { get; set; } = default!;
    [Inject] private LocalizationService L { get; set; } = default!;
    [Inject] private ProjectService Project { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private BrandingService Branding { get; set; } = default!;
    [Inject] private MaterialService MaterialSvc { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    private int _languageKey;
    private List<ProjectDto> _projects = [];
    private bool _initialLoad = true;
    private bool _startupError;
    private bool _projectsLoaded;

    protected override async Task OnInitializedAsync()
    {
        L.OnLanguageChanged += () => { _languageKey++; StateHasChanged(); };
        Project.OnProjectChanged += OnProjectChanged;
        Branding.OnBrandingChanged += StateHasChanged;
        Nav.LocationChanged += OnLocationChanged;
        try
        {
            await L.InitializeAsync();
            await Branding.InitializeAsync();
            await Auth.InitializeAsync();
            await Project.InitializeAsync();
            await LoadProjectsAsync();
            CheckProjectRedirect();
            _ = MaterialSvc.EnsureCacheAsync();
        }
        catch { _startupError = true; }
        finally { _initialLoad = false; }
    }

    private void Reload() => Nav.NavigateTo(Nav.Uri, forceLoad: true);

    private void OnProjectChanged()
    {
        MaterialSvc.InvalidateCache();
        _ = InvokeAsync(async () =>
        {
            await LoadProjectsAsync();
            _ = MaterialSvc.EnsureCacheAsync();
            StateHasChanged();
        });
    }

    private async Task LoadProjectsAsync()
    {
        try { _projects = await Project.GetMyProjectsAsync() ?? []; } catch { }
        _projectsLoaded = true;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        _ = InvokeAsync(() =>
        {
            CheckProjectRedirect();
            return Task.CompletedTask;
        });
    }

    private static readonly string[] AllowedWithoutProject =
        ["/projects", "/backup", "/settings", "/admin", "/privacy", "/imprint", "/terms", "/changelog"];

    private void CheckProjectRedirect()
    {
        if (!Auth.IsAuthenticated)
            return;
        if (!_projectsLoaded)
            return;

        var path = new Uri(Nav.Uri).AbsolutePath;

        if (Project.CurrentProject != null)
            return;

        if (AllowedWithoutProject.Any(a => path.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
            return;

        if (_projects.Count == 0)
            Nav.NavigateTo("/projects/new");
        else if (_projects.Count == 1)
            _ = InvokeAsync(async () => { await Project.SwitchProjectAsync(_projects[0]); Nav.NavigateTo("/"); });
        else
            Nav.NavigateTo("/projects");
    }

    private async Task SwitchProjectAsync(ProjectDto p)
    {
        await Project.SwitchProjectAsync(p);
        Nav.NavigateTo("/");
    }

    private async Task CloseOffcanvas()
    {
        try { await JSRuntime.InvokeVoidAsync("eval", "bootstrap.Offcanvas.getInstance(document.getElementById('navOffcanvas'))?.hide()"); } catch { }
    }
}
