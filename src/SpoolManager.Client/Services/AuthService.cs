using System.Net.Http.Json;
using Microsoft.JSInterop;
using SpoolManager.Shared.DTOs.Auth;

namespace SpoolManager.Client.Services;

// Shared installation state. No sign-in, bearer token, cookie or expiry.
public class AuthService(HttpClient http, IJSRuntime js)
{
    public UserDto? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser != null;
    public event Action? OnAuthStateChanged;

    public async Task InitializeAsync()
    {
        http.DefaultRequestHeaders.Authorization = null;
        await js.InvokeVoidAsync("localStorage.removeItem", "auth_token");
        CurrentUser = await http.GetFromJsonAsync<UserDto>("api/local/session");
        OnAuthStateChanged?.Invoke();
    }
    public async Task<bool> SetDashboardOnboardingDismissedAsync(bool dismissed)
    {
        using var response = await http.PutAsJsonAsync("api/local/dashboard-onboarding", new SetDashboardOnboardingRequest { Dismissed = dismissed });
        if (!response.IsSuccessStatusCode) return false;
        if (CurrentUser != null) CurrentUser.DashboardOnboardingDismissed = dismissed;
        return true;
    }
}
