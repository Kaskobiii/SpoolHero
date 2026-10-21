namespace SpoolManager.Shared.Models;

public class SpoolOwner
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
}
