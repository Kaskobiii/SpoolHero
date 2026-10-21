using SpoolManager.Infrastructure.Repositories;

namespace SpoolManager.Infrastructure.Services;

public interface IMaterialCatalogSettings
{
    Task<bool> IsOpenFilamentDbEnabledAsync();
    Task SetOpenFilamentDbEnabledAsync(bool enabled);
}

public class MaterialCatalogSettings(ISiteSettingsRepository settings) : IMaterialCatalogSettings
{
    private const string OpenFilamentDbKey = "openfilamentdb_enabled";
    private Task<bool>? _enabled;

    public Task<bool> IsOpenFilamentDbEnabledAsync() =>
        _enabled ??= ReadEnabledAsync();

    private async Task<bool> ReadEnabledAsync() =>
        !string.Equals(await settings.GetAsync(OpenFilamentDbKey), "false", StringComparison.OrdinalIgnoreCase);

    public async Task SetOpenFilamentDbEnabledAsync(bool enabled)
    {
        await settings.SetAsync(OpenFilamentDbKey, enabled ? "true" : "false");
        _enabled = Task.FromResult(enabled);
    }
}
