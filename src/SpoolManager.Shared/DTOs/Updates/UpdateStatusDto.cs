namespace SpoolManager.Shared.DTOs.Updates;

public class UpdateStatusDto
{
    public bool Supported { get; set; }
    public bool Busy { get; set; }
    public bool Available { get; set; }
    public string Phase { get; set; } = "idle";
    public string Message { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
}
