namespace SpoolManager.Shared.DTOs.Integrations;

public class SpoolmanIntegrationStatusDto
{
    public bool Enabled { get; set; }
    public DateTime? TokenCreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastUsageAt { get; set; }
    public int ConnectedPrinters { get; set; }
    public List<SpoolmanSpoolStatusDto> Spools { get; set; } = [];
}

public class SpoolmanSpoolStatusDto
{
    public Guid SpoolId { get; set; }
    public int Id { get; set; }
    public string[] CardUids { get; set; } = [];
    public decimal RemainingWeightGrams { get; set; }
    public string MaterialName { get; set; } = "";
    public bool CanTrackUsage { get; set; }
    public string? TrackingIssue { get; set; }
    public bool MissingDensity { get; set; }
    public bool MissingDiameter { get; set; }
}

public class SpoolmanConnectionDto
{
    public string ConnectionPath { get; set; } = "";
    public DateTime TokenCreatedAt { get; set; }
}
