namespace BaBaBooeyStrap.Core.Models;

public class State
{
    public bool TestModeWarningShown { get; set; }
    public bool IgnoreOutdatedChannel { get; set; }
    public bool WatcherRunning { get; set; }
    public bool PromptWebView2Install { get; set; } = true;
    public string? LastPage { get; set; }
    public bool ForceReinstall { get; set; }
    public WindowState SettingsWindow { get; set; } = new WindowState();
    public string? InstallLocation { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public bool OnboardingCompleted { get; set; }
    public DateTimeOffset? LastAppUpdateCheck { get; set; }
    public string? SkippedAppVersion { get; set; }
    public string LastTargetName { get; set; } = string.Empty;
    public string LastTargetInput { get; set; } = string.Empty;
    public string LastJobId { get; set; } = string.Empty;
    public string? FloatingBarMonitorDevice { get; set; }
    public double? FloatingBarLeft { get; set; }
    public double? FloatingBarTop { get; set; }
    public bool FloatingBarCollapsed { get; set; }
    private Dictionary<string, AccountWindowBounds> _accountWindowBounds = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, AccountWindowBounds> AccountWindowBounds
    {
        get => _accountWindowBounds;
        set
        {
            var dict = new Dictionary<string, AccountWindowBounds>(StringComparer.OrdinalIgnoreCase);
            if (value != null)
            {
                foreach (var kv in value)
                {
                    dict[kv.Key] = kv.Value;
                }
            }
            _accountWindowBounds = dict;
        }
    }

    // Deprecated migration getters
    public AppState? Player { private get; set; }
    public AppState? Studio { private get; set; }
    public List<string>? ModManifest { private get; set; }
    public AppState? GetDeprecatedPlayer() => Player;
    public AppState? GetDeprecatedStudio() => Studio;
    public List<string>? GetDeprecatedModManifest() => ModManifest;
}
