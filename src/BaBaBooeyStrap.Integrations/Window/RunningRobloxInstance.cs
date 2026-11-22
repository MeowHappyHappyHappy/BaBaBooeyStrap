namespace BaBaBooeyStrap.Integrations.Window;

public sealed record RunningRobloxInstance(
    int ProcessId,
    nint WindowHandle,
    int SlotIndex,
    Guid? AccountId,
    long? UserId,
    string? Username,
    string? DisplayName,
    string? Notes,
    bool IsExternal,
    bool IsMinimized,
    bool IsFocused,
    string? MonitorDeviceName,
    int MonitorIndex,
    DateTimeOffset StartedAtUtc)
{
    public string EffectiveLabel =>
        !string.IsNullOrWhiteSpace(Username)
            ? Username
            : "Roblox";

    public string ShortLabel => $"#{SlotIndex} {EffectiveLabel}";

    public string MonitorBadge => MonitorIndex > 0 ? $"M{MonitorIndex}" : "M1";

    public string DetailLabel =>
        !string.IsNullOrWhiteSpace(Notes)
            ? $"{EffectiveLabel} ({Notes})"
            : EffectiveLabel;
}
