namespace SpoolManager.Shared.Models;

public class SpoolmanIntegration
{
    public Guid ProjectId { get; set; }
    public string? TokenHash { get; set; }
    public DateTime? TokenCreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastUsageAt { get; set; }
    public int NextSpoolId { get; set; } = 1;
}

public class SpoolmanSpoolMap
{
    public Guid ProjectId { get; set; }
    public int Id { get; set; }
    public Guid SpoolId { get; set; }
    public string CardUids { get; set; } = "";
    // Retains fractions of a hundredth of a gram across Moonraker reports.
    public decimal UsageRemainderGrams { get; set; }
}
