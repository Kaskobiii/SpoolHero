using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpoolManager.Shared.DTOs.Updates;

namespace SpoolManager.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/updates")]
public class UpdatesController(IConfiguration configuration, IHttpClientFactory clients) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Status() => ForwardAsync(HttpMethod.Get, "status");

    [HttpPost("check")]
    public Task<IActionResult> Check() => ForwardAsync(HttpMethod.Post, "check");

    [HttpPost("install")]
    public Task<IActionResult> Install() => ForwardAsync(HttpMethod.Post, "update");

    private async Task<IActionResult> ForwardAsync(HttpMethod method, string path)
    {
        Response.Headers.CacheControl = "no-store";
        var token = configuration["Updates:Token"];
        var url = configuration["Updates:Url"];
        var installed = Environment.GetEnvironmentVariable("SPOOLHERO_REVISION") ?? "Local installation";
        if (string.IsNullOrWhiteSpace(token) || !Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
            return Ok(new UpdateStatusDto { Phase = "unavailable", CurrentVersion = installed,
                Message = "The Docker updater is not enabled on this installation. New Docker installs include it automatically; existing installs need scripts/enable-updater.sh once." });
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(baseUri.ToString().TrimEnd('/') + "/" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await clients.CreateClient("updater").SendAsync(request, HttpContext.RequestAborted);
            var status = await response.Content.ReadFromJsonAsync<UpdateStatusDto>(HttpContext.RequestAborted);
            if (status == null) return StatusCode(502, new { message = "No response from the Docker updater." });
            if (string.IsNullOrEmpty(status.CurrentVersion)) status.CurrentVersion = installed;
            return StatusCode((int)response.StatusCode, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Ok(new UpdateStatusDto { Phase = "unavailable", CurrentVersion = installed,
                Message = "Cannot reach the Docker updater. Check that its container is running." });
        }
    }
}
