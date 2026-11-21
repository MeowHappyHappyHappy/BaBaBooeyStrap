namespace BaBaBooeyStrap.Core.Models;

/// <summary>
/// Represents a named Roblox join link or place target saved by the user for quick reuse.
/// </summary>
public sealed class SavedJoinTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string TargetInput { get; set; } = string.Empty;
    public string? JobId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
