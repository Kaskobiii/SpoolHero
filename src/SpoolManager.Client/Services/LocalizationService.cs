using System.Net.Http.Json;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace SpoolManager.Client.Services;

public class LocalizationService
{
    private Dictionary<string, string> _strings = new();
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;

    public string CurrentLanguage { get; private set; } = "en";

    public event Action? OnLanguageChanged;

    public string this[string key] =>
        _strings.TryGetValue(key, out var value) ? value : key;

    public LocalizationService(HttpClient http, IJSRuntime js)
    {
        _http = http;
        _js = js;
    }

    public async Task InitializeAsync()
    {
        string? manualLang = null;
        try { manualLang = await _js.InvokeAsync<string?>("localStorage.getItem", "lang_manual"); } catch { }
        await SetLanguageAsync(manualLang ?? "en");
    }

    public async Task SetLanguageAsync(string lang)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"i18n/{lang}.json");
            request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            if (dict != null)
            {
                _strings = dict;
                CurrentLanguage = lang;
                try { await _js.InvokeVoidAsync("spoolHeroSetDocMeta", lang); } catch { }
                OnLanguageChanged?.Invoke();
            }
        }
        catch { }
    }

    public async Task SetManualLanguageAsync(string lang)
    {
        await SetLanguageAsync(lang);
        try { await _js.InvokeVoidAsync("localStorage.setItem", "lang_manual", lang); } catch { }
    }

    public string Format(string key, params object[] args)
    {
        var template = this[key];
        for (var i = 0; i < args.Length; i++)
            template = template.Replace($"{{{i}}}", args[i]?.ToString() ?? string.Empty);
        return template;
    }
}
