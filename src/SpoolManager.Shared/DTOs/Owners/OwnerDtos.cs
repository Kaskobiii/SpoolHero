using System.ComponentModel.DataAnnotations;

namespace SpoolManager.Shared.DTOs.Owners;

public class OwnerDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class SaveOwnerRequest
{
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;
}
