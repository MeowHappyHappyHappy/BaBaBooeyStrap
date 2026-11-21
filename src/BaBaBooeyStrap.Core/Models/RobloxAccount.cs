namespace BaBaBooeyStrap.Core.Models;

public sealed class RobloxAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string Cookie { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLaunchedAt { get; set; }
    public RobloxClientSettingsProfile? RobloxSettings { get; set; }
}
