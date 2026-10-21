using Microsoft.AspNetCore.Components;
using SpoolManager.Client.Services;
using SpoolManager.Shared.DTOs.Materials;

namespace SpoolManager.Client.Pages.Materials;

public partial class MaterialForm
{
    [Inject] private MaterialService Materials { get; set; } = default!;
    [Inject] private LocalizationService L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private ProjectService Project { get; set; } = default!;

    [Parameter] public Guid? Id { get; set; }

    private static readonly (string hex, string name)[] ColorPresets =
    [
        ("1A1A1A","Schwarz"),("F2F2F2","Weiß"),("808080","Grau"),("C0C0C0","Silber"),
        ("FF0000","Rot"),("8B0000","Dunkelrot"),("FF6400","Orange"),("FFFF00","Gelb"),
        ("00FF00","Grün"),("006400","Dunkelgrün"),("0000FF","Blau"),("ADD8E6","Hellblau"),
        ("800080","Lila"),("FF69B4","Pink"),("8B4513","Braun"),("FFE4C4","Natur")
    ];

    private bool _loading = true;
    private bool _loadFailed;
    private bool _saving;
    private bool _isEditing;
    private bool _isColorVariant;
    private string? _error;
    private string? _success;
    private CreateMaterialRequest _form = new();
    private string FormTitle => _isEditing ? L["material.edit"]
        : _isColorVariant ? L["material.color.variant"] : L["material.add"];

    private string _diameterStr
    {
        get => _form.DiameterMm.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (decimal.TryParse(value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                _form.DiameterMm = d;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        // The same component handles all three routes. Determine the mode from the
        // route so a previous Id parameter cannot turn a new color into an update.
        var path = new Uri(Nav.Uri).AbsolutePath.TrimEnd('/');
        _isColorVariant = path.EndsWith("/color-variant", StringComparison.OrdinalIgnoreCase);
        _isEditing = Id.HasValue && path.EndsWith("/edit", StringComparison.OrdinalIgnoreCase);
        _form = new();
        _success = null;
        _error = null;
        _loading = true;
        _loadFailed = false;

        if (Project.CurrentProject == null)
        {
            _loading = false;
            return;
        }

        try
        {
            if ((_isEditing || _isColorVariant) && Id.HasValue)
            {
                var material = await Materials.GetByIdAsync(Id.Value);
                if (material == null)
                {
                    _error = L["material.load.error"];
                    _loadFailed = true;
                }
                else
                    _form = CopyMaterial(material, _isColorVariant);
            }
        }
        catch (HttpRequestException)
        {
            _error = L["material.load.error"];
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private static CreateMaterialRequest CopyMaterial(FilamentMaterialDto material, bool newColor) => new()
    {
        Type = material.Type, ColorHex = newColor ? "FFFFFF" : material.ColorHex, Brand = material.Brand,
        MinTempCelsius = material.MinTempCelsius, MaxTempCelsius = material.MaxTempCelsius,
        ColorName = newColor ? null : material.ColorName, DiameterMm = material.DiameterMm,
        WeightGrams = material.WeightGrams, BedTempCelsius = material.BedTempCelsius,
        DensityGCm3 = material.DensityGCm3, DryTempCelsius = material.DryTempCelsius,
        DryTimeHours = material.DryTimeHours, Notes = material.Notes,
        ReorderUrl = material.ReorderUrl, PricePerKg = material.PricePerKg, IsPublic = material.IsPublic
    };

    private void OnColorPick(ChangeEventArgs e)
    {
        var hex = e.Value?.ToString()?.TrimStart('#').ToUpper() ?? "FFFFFF";
        _form.ColorHex = hex;
    }

    private Task SaveAsync() => SaveAsync(false);

    private async Task SaveAsync(bool addAnotherColor)
    {
        if (_saving || _loading || _loadFailed)
            return;

        _error = null;
        _success = null;
        _form.Type = _form.Type.Trim();
        _form.Brand = _form.Brand.Trim();
        _form.ColorHex = _form.ColorHex.Trim().TrimStart('#').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(_form.Type) || string.IsNullOrWhiteSpace(_form.Brand))
        {
            _error = L["material.invalid.required"];
            return;
        }
        if (_form.ColorHex.Length != 6 || !_form.ColorHex.All(Uri.IsHexDigit))
        {
            _error = L["material.invalid.color"];
            return;
        }

        _saving = true;
        try
        {
            using var resp = _isEditing
                ? await Materials.UpdateAsync(Id!.Value, new UpdateMaterialRequest
                {
                    Type = _form.Type, ColorHex = _form.ColorHex, Brand = _form.Brand,
                    MinTempCelsius = _form.MinTempCelsius, MaxTempCelsius = _form.MaxTempCelsius,
                    ColorName = _form.ColorName, DiameterMm = _form.DiameterMm, WeightGrams = _form.WeightGrams,
                    BedTempCelsius = _form.BedTempCelsius, DensityGCm3 = _form.DensityGCm3,
                    DryTempCelsius = _form.DryTempCelsius, DryTimeHours = _form.DryTimeHours,
                    Notes = _form.Notes, ReorderUrl = _form.ReorderUrl, PricePerKg = _form.PricePerKg,
                    IsPublic = _form.IsPublic
                })
                : await Materials.CreateAsync(_form);

            if (!resp.IsSuccessStatusCode)
            {
                _error = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(_error))
                    _error = L["material.save.error"];
                return;
            }

            if (addAnotherColor && !_isEditing)
            {
                var savedColor = string.IsNullOrWhiteSpace(_form.ColorName)
                    ? $"#{_form.ColorHex}" : _form.ColorName;
                _success = L.Format("material.color.saved", savedColor);
                _form.ColorHex = "FFFFFF";
                _form.ColorName = null;
            }
            else
                Nav.NavigateTo("/materials");
        }
        catch (HttpRequestException)
        {
            _error = L["material.save.error"];
        }
        finally
        {
            _saving = false;
        }
    }
}
