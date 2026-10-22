using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using SpoolManager.Client.Services;
using SpoolManager.Shared.DTOs.Backup;

namespace SpoolManager.Client.Pages.Backup;

public partial class BackupPage
{
    private const long MaxBackupBytes = InventoryBackupDocument.MaxFileBytes;
    [Inject] private BackupService Backups { get; set; } = default!;
    [Inject] private AuthService Auth { get; set; } = default!;
    [Inject] private ProjectService Project { get; set; } = default!;
    [Inject] private MaterialService Materials { get; set; } = default!;
    [Inject] private FavoriteService Favorites { get; set; } = default!;
    [Inject] private LocalizationService L { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    private bool _exporting;
    private bool _reading;
    private bool _importing;
    private bool _exported;
    private bool _applyCatalogSetting;
    private int _fileKey;
    private byte[]? _file;
    private string _fileName = string.Empty;
    private string? _exportError;
    private string? _importError;
    private BackupSummary? _summary;
    private BackupImportResultDto? _importResult;
    private bool _projectSelected;
    private bool Busy => _exporting || _reading || _importing;

    private async Task ExportAsync()
    {
        if (Busy) return;
        _exporting = true;
        _exportError = null;
        _exported = false;
        try
        {
            using var response = await Backups.ExportAsync();
            if (!response.IsSuccessStatusCode)
            {
                _exportError = response.StatusCode == HttpStatusCode.Unauthorized
                    ? L["backup.sign.in"]
                    : await BackupService.GetErrorAsync(response, L["backup.export.error"]);
                return;
            }
            var file = await response.Content.ReadAsByteArrayAsync();
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? $"SpoolHero-backup-{DateTime.UtcNow:yyyy-MM-dd}.json";
            await JS.InvokeVoidAsync("downloadHelper.saveAs", file, fileName);
            _exported = true;
        }
        catch (HttpRequestException) { _exportError = L["backup.connection.error"]; }
        catch (TaskCanceledException) { _exportError = L["backup.connection.error"]; }
        catch (JSException) { _exportError = L["backup.export.error"]; }
        finally { _exporting = false; }
    }

    private async Task ChooseBackupAsync(InputFileChangeEventArgs e)
    {
        _reading = true;
        _file = null;
        _summary = null;
        _importResult = null;
        _projectSelected = false;
        _importError = null;
        _applyCatalogSetting = false;
        _fileName = e.File.Name;
        try
        {
            if (e.File.Size > MaxBackupBytes)
            {
                _importError = L["backup.file.too.large"];
                return;
            }
            await using var stream = e.File.OpenReadStream(MaxBackupBytes);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var file = buffer.ToArray();
            using var document = JsonDocument.Parse(file);
            var data = document.RootElement;
            if (data.GetProperty("format").GetString() != InventoryBackupDocument.CurrentFormat
                || data.GetProperty("version").GetInt32() != InventoryBackupDocument.CurrentVersion)
            {
                _importError = L["backup.file.invalid"];
                return;
            }
            _summary = new BackupSummary
            {
                ProjectCount = data.GetProperty("projects").GetArrayLength(),
                MaterialCount = data.GetProperty("materials").GetArrayLength(),
                OwnerCount = data.GetProperty("owners").GetArrayLength(),
                SpoolCount = data.GetProperty("spools").GetArrayLength(),
                PrinterCount = data.GetProperty("printers").GetArrayLength(),
                StorageLocationCount = data.GetProperty("storageLocations").GetArrayLength(),
                DryerCount = data.GetProperty("dryers").GetArrayLength(),
                FavoriteCount = data.GetProperty("materialFavorites").GetArrayLength(),
                ExportedAt = data.GetProperty("exportedAt").GetDateTimeOffset(),
                HasCatalogSetting = data.TryGetProperty("openFilamentDbEnabled", out var setting)
                    && setting.ValueKind is JsonValueKind.True or JsonValueKind.False,
                SizeBytes = file.LongLength
            };
            _file = file;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { _importError = L["backup.file.invalid"]; }
        catch (IOException) { _importError = L["backup.file.read.error"]; }
        finally { _reading = false; }
    }

    private async Task ImportAsync()
    {
        if (Busy || _file == null) return;
        _importing = true;
        _importError = null;
        try
        {
            using var response = await Backups.ImportAsync(_file, _applyCatalogSetting);
            if (!response.IsSuccessStatusCode)
            {
                _importError = response.StatusCode switch
                {
                    HttpStatusCode.RequestEntityTooLarge => L["backup.file.too.large"],
                    HttpStatusCode.Forbidden => L["backup.import.admin"],
                    HttpStatusCode.Unauthorized => L["backup.sign.in"],
                    _ => await BackupService.GetErrorAsync(response, L["backup.import.error"])
                };
                return;
            }
            _importResult = await response.Content.ReadFromJsonAsync<BackupImportResultDto>() ?? throw new JsonException();
            _file = null;
            _summary = null;
            _fileKey++;
            Materials.InvalidateCache();
            Favorites.InvalidateCache();
            // Refresh the project selector and choose the first restored inventory.
            try
            {
                var projects = await Project.GetMyProjectsAsync() ?? [];
                var restored = projects.FirstOrDefault(p => _importResult?.ProjectIds.Contains(p.Id) == true);
                if (restored != null)
                {
                    await Project.SwitchProjectAsync(restored);
                    _projectSelected = true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JSException)
            { /* The import succeeded; its projects remain available from the project list. */ }
        }
        catch (HttpRequestException) { _importError = L["backup.connection.error"]; }
        catch (TaskCanceledException) { _importError = L["backup.connection.error"]; }
        catch (JsonException) { _importError = L["backup.import.error"]; }
        finally { _importing = false; }
    }

    private sealed class BackupSummary
    {
        public int ProjectCount { get; init; }
        public int MaterialCount { get; init; }
        public int OwnerCount { get; init; }
        public int SpoolCount { get; init; }
        public int PrinterCount { get; init; }
        public int StorageLocationCount { get; init; }
        public int DryerCount { get; init; }
        public int FavoriteCount { get; init; }
        public DateTimeOffset ExportedAt { get; init; }
        public bool HasCatalogSetting { get; init; }
        public long SizeBytes { get; init; }
    }

}
