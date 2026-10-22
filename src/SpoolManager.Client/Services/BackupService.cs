using System.Net.Http.Headers;
using System.Text.Json;

namespace SpoolManager.Client.Services;

public class BackupService(HttpClient http)
{
    public Task<HttpResponseMessage> ExportAsync() => http.GetAsync("api/backup/export");

    public async Task<HttpResponseMessage> ImportAsync(byte[] file, bool applyCatalogSetting)
    {
        using var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await http.PostAsync($"api/backup/import?applyCatalogSetting={applyCatalogSetting.ToString().ToLowerInvariant()}", content);
    }

    public static async Task<string> GetErrorAsync(HttpResponseMessage response, string fallback)
    {
        try
        {
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (error.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString()))
                return message.GetString()!;
            if (error.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .Where(e => e.Value.ValueKind == JsonValueKind.Array)
                    .SelectMany(e => e.Value.EnumerateArray())
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .Take(3);
                var text = string.Join(" ", messages);
                if (text.Length > 0) return text;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return fallback;
    }
}
