using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SpoolManager.Client.Services;
using SpoolManager.Shared.DTOs.Dryers;
using SpoolManager.Shared.DTOs.Materials;
using SpoolManager.Shared.DTOs.Printers;
using SpoolManager.Shared.DTOs.Spools;
using SpoolManager.Shared.DTOs.Owners;
using SpoolManager.Shared.DTOs.Storage;
using System.Net.Http.Json;
using System.Text.Json;

namespace SpoolManager.Client.Pages.Spools;

public partial class SpoolForm
{
    [Parameter] public Guid? Id { get; set; }
    [Inject] private OwnerService Owners { get; set; } = default!;
    [Inject] private SpoolService Spools { get; set; } = default!;
    [Inject] private MaterialService Materials { get; set; } = default!;
    [Inject] private PrinterService Printers { get; set; } = default!;
    [Inject] private StorageService Storage { get; set; } = default!;
    [Inject] private DryerService Dryers { get; set; } = default!;
    [Inject] private NfcService Nfc { get; set; } = default!;
    [Inject] private LocalizationService L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private ProjectService Project { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;

    private bool _loading = true;
    private bool _saving;
    private string? _error;
    private CreateSpoolRequest _form = new();
    private decimal? _weighedTotalGrams;
    private DateTime? _expectedUpdatedAt;
    private List<OwnerDto> _owners = [];
    private List<PrinterDto> _printers = [];
    private List<StorageLocationDto> _storages = [];
    private List<DryerDto> _dryerList = [];

    private bool _nfcAvailable;
    private bool _nfcReading;
    private bool _nfcImportSuccess;
    private string? _nfcImportError;
    private DotNetObjectReference<SpoolForm>? _dotnetRef;

    protected override async Task OnInitializedAsync()
    {
        if (Project.CurrentProject == null)
            return;
        var pt = Printers.GetAllAsync();
        var st = Storage.GetAllAsync();
        var dt = Dryers.GetAllAsync();
        var ot = Owners.GetAllAsync();
        await Task.WhenAll(pt, st, dt, ot);
        _owners = await ot ?? [];
        _printers = await pt ?? [];
        _storages = await st ?? [];
        _dryerList = await dt ?? [];

        if (Id.HasValue)
        {
            var spool = await Spools.GetByIdAsync(Id.Value);
            if (spool != null)
            {
                _expectedUpdatedAt = spool.UpdatedAt;
                _form.FilamentMaterialId = spool.FilamentMaterialId;
                _form.RemainingWeightGrams = spool.RemainingWeightGrams;
                _form.EmptySpoolWeightGrams = spool.EmptySpoolWeightGrams;
                _form.InitialWeightGrams = spool.InitialWeightGrams;
                _form.PrinterId = spool.PrinterId;
                _form.StorageLocationId = spool.StorageLocationId;
                _form.DryerId = spool.DryerId;
                _form.PurchasedAt = spool.PurchasedAt;
                _form.PurchasePrice = spool.PurchasePrice;
                _form.RfidTagUid = spool.RfidTagUid;
                _form.ReorderUrl = spool.ReorderUrl;
                _form.OwnerId = spool.OwnerId;
                _form.Notes = spool.Notes;
            }
        }
        else
        {
            _nfcAvailable = await Nfc.CheckSupportAsync();
        }
        _loading = false;
    }

    private async Task StartNfcScanAsync()
    {
        _nfcImportError = null;
        _nfcImportSuccess = false;
        _nfcReading = true;
        _dotnetRef ??= DotNetObjectReference.Create(this);
        try { await Nfc.StartReadAsync(_dotnetRef); }
        catch { _nfcReading = false; _nfcImportError = L["spool.nfc.error"]; }
    }

    private async Task StopNfcScanAsync()
    {
        _nfcReading = false;
        try { await Nfc.StopReadAsync(); } catch { }
    }

    [JSInvokable]
    public async void OnTagRead(string json)
    {
        _nfcReading = false;
        _nfcImportError = null;
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var brand = root.TryGetProperty("brand", out var b) ? b.GetString() : null;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var colorHex = root.TryGetProperty("color_hex", out var c) ? c.GetString()?.TrimStart('#') : null;

            if (!string.IsNullOrEmpty(brand) || !string.IsNullOrEmpty(type))
            {
                var searchQ = $"{brand} {type}".Trim();
                var candidates = await Http.GetFromJsonAsync<List<FilamentMaterialDto>>(
                    $"api/materials/search?q={Uri.EscapeDataString(searchQ)}&limit=20") ?? [];

                var match = candidates.FirstOrDefault(m =>
                    string.Equals(m.Brand, brand, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Type, type, StringComparison.OrdinalIgnoreCase) &&
                    (colorHex == null || string.Equals(m.ColorHex, colorHex, StringComparison.OrdinalIgnoreCase)));

                match ??= candidates.FirstOrDefault(m =>
                    string.Equals(m.Brand, brand, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Type, type, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    _form.FilamentMaterialId = match.Id;
                    if (match.WeightGrams is > 0)
                    {
                        _form.RemainingWeightGrams = match.WeightGrams.Value;
                        _form.InitialWeightGrams = match.WeightGrams.Value;
                    }
                    if (string.IsNullOrEmpty(_form.ReorderUrl) && match.ReorderUrl != null)
                        _form.ReorderUrl = match.ReorderUrl;
                    _nfcImportSuccess = true;
                }
                else
                {
                    _nfcImportError = L["spool.nfc.import.no.match"]
                        .Replace("{brand}", brand ?? "?")
                        .Replace("{type}", type ?? "?");
                }
            }
            else
            {
                _nfcImportError = L["spool.nfc.import.invalid"];
            }
        }
        catch
        {
            _nfcImportError = L["spool.nfc.import.invalid"];
        }
        InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public void OnReadError(string error)
    {
        _nfcReading = false;
        _nfcImportError = L["spool.nfc.error"];
        InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public void OnScanStarted() => InvokeAsync(StateHasChanged);

    [JSInvokable]
    public void OnWriteSuccess() { }

    [JSInvokable]
    public void OnWriteError(string error) { }

    private void OnMaterialPicked(FilamentMaterialDto? mat)
    {
        if (mat == null)
            return;
        _form.FilamentMaterialId = mat.Id;
        if (Id.HasValue)
            return;
        if (mat.WeightGrams is > 0)
        {
            _form.RemainingWeightGrams = mat.WeightGrams.Value;
            _form.InitialWeightGrams = mat.WeightGrams.Value;
        }
        if (string.IsNullOrEmpty(_form.ReorderUrl) && mat.ReorderUrl != null)
            _form.ReorderUrl = mat.ReorderUrl;
    }

    private bool CanApplyWeighedTotal => _form.EmptySpoolWeightGrams is decimal tare && tare >= 0
        && _weighedTotalGrams is decimal gross && gross >= tare;

    private void ApplyWeighedTotal()
    {
        if (CanApplyWeighedTotal)
            _form.RemainingWeightGrams = Math.Round(_weighedTotalGrams!.Value - _form.EmptySpoolWeightGrams!.Value, 1);
    }

    private bool HasNoLocation => !_form.PrinterId.HasValue && !_form.StorageLocationId.HasValue && !_form.DryerId.HasValue;

    private void OnLocationChange(string? type, Guid? id)
    {
        _form.PrinterId = type == "printer" ? id : null;
        _form.StorageLocationId = type == "storage" ? id : null;
        _form.DryerId = type == "dryer" ? id : null;
    }

    private async Task SaveAsync()
    {
        if (_form.FilamentMaterialId == Guid.Empty)
        {
            _error = L["spool.material.required"];
            return;
        }
        _saving = true;
        _error = null;

        HttpResponseMessage resp;
        if (Id.HasValue)
        {
            resp = await Spools.UpdateAsync(Id.Value, new UpdateSpoolRequest
            {
                ExpectedUpdatedAt = _expectedUpdatedAt,
                FilamentMaterialId = _form.FilamentMaterialId,
                RemainingWeightGrams = _form.RemainingWeightGrams,
                EmptySpoolWeightGrams = _form.EmptySpoolWeightGrams,
                InitialWeightGrams = _form.InitialWeightGrams,
                RfidTagUid = _form.RfidTagUid,
                PrinterId = _form.PrinterId,
                StorageLocationId = _form.StorageLocationId,
                DryerId = _form.DryerId,
                PurchasedAt = _form.PurchasedAt,
                PurchasePrice = _form.PurchasePrice,
                ReorderUrl = _form.ReorderUrl,
                OwnerId = _form.OwnerId,
                Notes = _form.Notes
            });
        }
        else
        {
            resp = await Spools.CreateAsync(_form);
        }

        _saving = false;
        if (resp.IsSuccessStatusCode)
            Nav.NavigateTo("/spools");
        else if (Id.HasValue && (int)resp.StatusCode == 409)
            _error = L["spool.edit.conflict"];
        else
            _error = await resp.Content.ReadAsStringAsync();
    }
}
