using System.Net;
using Microsoft.AspNetCore.Components;
using SpoolManager.Client.Services;
using SpoolManager.Shared.DTOs.Owners;

namespace SpoolManager.Client.Pages.Owners;

public partial class OwnerList
{
    [Inject] private OwnerService Owners { get; set; } = default!;
    [Inject] private LocalizationService L { get; set; } = default!;
    [Inject] private ProjectService Project { get; set; } = default!;
    private List<OwnerDto> _owners = [];
    private string _name = string.Empty;
    private Guid? _editingId;
    private bool _loading = true;
    private bool _saving;
    private bool _saved;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        if (Project.CurrentProject == null) return;
        try { _owners = await Owners.GetAllAsync() ?? []; }
        catch { _error = L["common.error"]; }
        finally { _loading = false; }
    }

    private void Edit(OwnerDto owner)
    {
        _editingId = owner.Id;
        _name = owner.Name;
        _error = null;
        _saved = false;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _name = string.Empty;
        _error = null;
        _saved = false;
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        _saved = false;
        _error = null;
        if (string.IsNullOrWhiteSpace(_name) || _name.Length > 100)
        {
            _error = L["owner.required"];
            return;
        }
        _saving = true;
        try
        {
            var request = new SaveOwnerRequest { Name = _name.Trim() };
            using var response = _editingId.HasValue
                ? await Owners.UpdateAsync(_editingId.Value, request)
                : await Owners.CreateAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _error = L[response.StatusCode == HttpStatusCode.Conflict ? "owner.duplicate" : "common.error"];
                return;
            }
            CancelEdit();
            _owners = await Owners.GetAllAsync() ?? [];
            _saved = true;
        }
        catch { _error = L["common.error"]; }
        finally { _saving = false; }
    }
}
