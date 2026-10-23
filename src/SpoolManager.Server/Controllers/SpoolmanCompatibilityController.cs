using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpoolManager.Infrastructure.Services;
using SpoolManager.Server.Services;

namespace SpoolManager.Server.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/spoolman/{token}/api/v1")]
public class SpoolmanCompatibilityController(SpoolmanService spoolman, SpoolmanConnections connections,
    IServiceScopeFactory scopes) : ControllerBase
{
    private async Task<Guid?> ProjectAsync(string token)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return await spoolman.AuthenticateAsync(token);
    }
    private IActionResult Missing() => NotFound(new { message = "Not found." });

    [HttpGet("info")]
    public async Task<IActionResult> Info(string token)
    {
        if (await ProjectAsync(token) == null) return Missing();
        return Ok(new { version = "0.22.0", debug_mode = false, automatic_backups = false,
            data_directory = "", logs_directory = "", backups_directory = "",
            server = "SpoolHero compatibility", capabilities = new[] { "spool-read", "card-uids", "spool-use" } });
    }

    private static object? Field(string entity, string? key = null) => (entity, key) switch
    {
        ("spool", null or "card_uids") => new { key = "card_uids", name = "Card UIDs", entity_type = "spool", field_type = "text", order = 1, default_value = "\"\"" },
        ("filament", null or "variant") => new { key = "variant", name = "Variant", entity_type = "filament", field_type = "text", order = 1, default_value = "\"\"" },
        _ => null
    };
    [HttpGet("field/{entity}")]
    public async Task<IActionResult> Fields(string token, string entity)
    {
        if (await ProjectAsync(token) == null) return Missing();
        var field = Field(entity); return field == null ? Missing() : Ok(new[] { field });
    }
    [HttpPost("field/{entity}/{key}")]
    public async Task<IActionResult> EnsureField(string token, string entity, string key, [FromBody] JsonElement body)
    {
        if (await ProjectAsync(token) == null) return Missing();
        var field = Field(entity, key); return field == null ? Missing() : Ok(field);
    }

    [HttpGet("spool")]
    public async Task<IActionResult> Spools(string token, [FromQuery] int? limit = null,
        [FromQuery] int offset = 0, [FromQuery(Name = "allow_archived")] bool allowArchived = false)
    {
        var project = await ProjectAsync(token);
        if (project == null) return Missing();
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            await WebSocketAsync(project.Value, token);
            return new EmptyResult();
        }
        if (offset < 0 || limit is < 0 or > 100_000)
            return BadRequest(new { message = "Invalid pagination; offset must be nonnegative and limit at most 100000." });
        var rows = await spoolman.GetRowsAsync(project.Value, allowArchived);
        Response.Headers["X-Total-Count"] = rows.Count.ToString();
        return Ok(rows.Skip(offset).Take(limit ?? rows.Count).Select(SpoolmanService.ToWire));
    }
    [HttpGet("spool/{id:int}")]
    public async Task<IActionResult> Spool(string token, int id)
    {
        var project = await ProjectAsync(token); if (project == null) return Missing();
        var row = await spoolman.GetRowAsync(project.Value, id);
        return row == null ? Missing() : Ok(SpoolmanService.ToWire(row));
    }
    [HttpPatch("spool/{id:int}")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Patch(string token, int id, [FromBody] JsonElement body)
    {
        var project = await ProjectAsync(token); if (project == null) return Missing();
        if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any(p => p.Name != "extra") ||
            !body.TryGetProperty("extra", out var extra) || extra.ValueKind != JsonValueKind.Object ||
            extra.EnumerateObject().Any(p => p.Name != "card_uids") ||
            !extra.TryGetProperty("card_uids", out var cards) || cards.ValueKind != JsonValueKind.String)
            return BadRequest(new { message = "This compatibility endpoint only accepts extra.card_uids. Edit spool properties in SpoolHero." });
        try
        {
            var row = await spoolman.SetCardUidsAsync(project.Value, id, cards.GetString()!);
            return row == null ? Missing() : Ok(SpoolmanService.ToWire(row));
        }
        catch (SpoolmanValidationException error) { return BadRequest(new { message = error.Message }); }
    }
    [HttpPut("spool/{id:int}/use")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> Use(string token, int id, [FromBody] JsonElement body)
    {
        var project = await ProjectAsync(token); if (project == null) return Missing();
        if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any(p => p.Name is not ("use_length" or "use_weight")))
            return BadRequest(new { message = "Supply use_length or use_weight." });
        decimal? length = null, weight = null;
        if (body.TryGetProperty("use_length", out var l))
        { if (!l.TryGetDecimalSafe(out var v)) return BadRequest(new { message = "use_length must be a finite number." }); length = v; }
        if (body.TryGetProperty("use_weight", out var w))
        { if (!w.TryGetDecimalSafe(out var v)) return BadRequest(new { message = "use_weight must be a finite number." }); weight = v; }
        try
        {
            var row = await spoolman.UseAsync(project.Value, id, length, weight);
            return row == null ? Missing() : Ok(SpoolmanService.ToWire(row));
        }
        catch (SpoolmanValidationException error) { return UnprocessableEntity(new { message = error.Message }); }
    }

    [HttpGet("{**path}")]
    [HttpPost("{**path}")]
    [HttpPut("{**path}")]
    [HttpPatch("{**path}")]
    [HttpDelete("{**path}")]
    public IActionResult Unsupported() => Missing();

    private async Task WebSocketAsync(Guid project, string token)
    {
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        connections.Add(project);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        var receive = ReceiveAsync(socket, cancellation.Token);
        var previous = new Dictionary<int, DateTime>();
        try
        {
            while (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                // Each poll uses a fresh scoped database. Rotation/disable invalidates existing sockets too.
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<SpoolmanService>();
                if (await service.AuthenticateAsync(token) != project)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Connection disabled or rotated.", CancellationToken.None);
                    break;
                }
                var rows = await service.GetRowsAsync(project, true);
                var current = rows.ToDictionary(x => x.Map.Id, x => x.Spool.UpdatedAt);
                foreach (var deleted in previous.Keys.Except(current.Keys))
                    await SendAsync(socket, new { resource = "spool", type = "deleted", date = DateTime.UtcNow, payload = new { id = deleted } }, cancellation.Token);
                foreach (var row in rows.Where(x => previous.TryGetValue(x.Map.Id, out var stamp) && stamp != x.Spool.UpdatedAt))
                    await SendAsync(socket, new { resource = "spool", type = "updated", date = DateTime.UtcNow, payload = SpoolmanService.ToWire(row) }, cancellation.Token);
                previous = current;
                var delay = Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
                if (await Task.WhenAny(receive, delay) == receive) break;
                await delay;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            cancellation.Cancel();
            try { await receive; } catch (OperationCanceledException) { } catch (WebSocketException) { }
            connections.Remove(project);
        }
    }
    private static async Task ReceiveAsync(WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                return;
            }
            if (result.Count > 0)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType, "This socket only publishes spool events.", CancellationToken.None);
                return;
            }
        }
    }
    private static Task SendAsync(WebSocket socket, object value, CancellationToken cancellation) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, cancellation);
}

internal static class SpoolmanJsonExtensions
{
    internal static bool TryGetDecimalSafe(this JsonElement value, out decimal result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out result);
    }
}
