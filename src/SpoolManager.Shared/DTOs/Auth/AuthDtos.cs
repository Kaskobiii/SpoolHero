namespace SpoolManager.Shared.DTOs.Auth;

public class SetLanguageRequest
{
    public string Language { get; set; } = "de";
}

public class SetDashboardOnboardingRequest
{
    public bool Dismissed { get; set; }
}

public class UserDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsPlatformAdmin { get; set; }
    public bool IsSuperAdmin { get; set; }
    public bool DashboardOnboardingDismissed { get; set; }
    public DateTime CreatedAt { get; set; }
}
