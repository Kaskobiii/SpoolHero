using System.Net.Http.Json;
using Microsoft.JSInterop;
using SpoolManager.Shared.DTOs.Projects;

namespace SpoolManager.Client.Services;

public class ProjectService
{
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;

    public ProjectDto? CurrentProject { get; private set; }
    public event Action? OnProjectChanged;

    public ProjectService(HttpClient http, IJSRuntime js)
    {
        _http = http;
        _js = js;
    }

    public async Task InitializeAsync()
    {
        var projects = await GetMyProjectsAsync() ?? [];
        var stored = await _js.InvokeAsync<string?>("localStorage.getItem", "project_id");
        var selected = Guid.TryParse(stored, out var id) ? projects.FirstOrDefault(p => p.Id == id) : null;
        selected ??= projects.OrderBy(p => p.CreatedAt).FirstOrDefault();
        if (selected != null) await SwitchProjectAsync(selected);
    }

    public Task<List<ProjectDto>?> GetMyProjectsAsync() =>
        _http.GetFromJsonAsync<List<ProjectDto>>("api/projects");

    public Task<HttpResponseMessage> CreateAsync(CreateProjectRequest request) =>
        _http.PostAsJsonAsync("api/projects", request);

    public Task<HttpResponseMessage> UpdateAsync(Guid id, UpdateProjectRequest request) =>
        _http.PutAsJsonAsync($"api/projects/{id}", request);

    public Task<HttpResponseMessage> DeleteAsync(Guid id) =>
        _http.DeleteAsync($"api/projects/{id}");

    public async Task SwitchProjectAsync(ProjectDto project)
    {
        CurrentProject = project;
        SetProjectHeader(project.Id);
        await _js.InvokeVoidAsync("localStorage.setItem", "project_id", project.Id.ToString());
        OnProjectChanged?.Invoke();
    }

    public async Task ClearProjectAsync()
    {
        CurrentProject = null;
        _http.DefaultRequestHeaders.Remove("X-Project-Id");
        try { await _js.InvokeVoidAsync("localStorage.removeItem", "project_id"); } catch { }
        OnProjectChanged?.Invoke();
    }

    private void SetProjectHeader(Guid projectId)
    {
        _http.DefaultRequestHeaders.Remove("X-Project-Id");
        _http.DefaultRequestHeaders.Add("X-Project-Id", projectId.ToString());
    }
}
