using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.Core.Models;

public class LaunchFlag
{
    public bool Active { get; set; }
    public string? Data { get; set; }
    public string Identifiers { get; }

    public LaunchFlag(string identifiers)
    {
        Identifiers = identifiers;
    }
}

public class LaunchSettings
{
    public LaunchFlag MenuFlag { get; } = new("preferences,menu,settings");
    public LaunchFlag WatcherFlag { get; } = new("watcher");
    public LaunchFlag BackgroundUpdaterFlag { get; } = new("backgroundupdater");
    public LaunchFlag QuietFlag { get; } = new("quiet");
    public LaunchFlag InstallFlag { get; } = new("install");
    public LaunchFlag UninstallFlag { get; } = new("uninstall");
    public LaunchFlag NoLaunchFlag { get; } = new("nolaunch");
    public LaunchFlag TestModeFlag { get; } = new("testmode");
    public LaunchFlag NoGPUFlag { get; } = new("nogpu");
    public LaunchFlag UpgradeFlag { get; } = new("upgrade");
    public LaunchFlag PlayerFlag { get; } = new("player");
    public LaunchFlag StudioFlag { get; } = new("studio");
    public LaunchFlag VersionFlag { get; } = new("version");
    public LaunchFlag ChannelFlag { get; } = new("channel");
    public LaunchFlag ForceFlag { get; } = new("force");
    public LaunchFlag BloxshadeFlag { get; } = new("bloxshade");
    public LaunchFlag HoldFlag { get; } = new("hold");
    public LaunchFlag AccountFlag { get; } = new("account");
    public LaunchFlag TargetFlag { get; } = new("target");
    public LaunchFlag JobIdFlag { get; } = new("jobid,job-id");
    public LaunchFlag NoHoldFlag { get; } = new("nohold,no-hold");
    public LaunchFlag DetachFlag { get; } = new("detach");

    public LaunchMode RobloxLaunchMode { get; set; } = LaunchMode.None;
    public string RobloxLaunchArgs { get; set; } = string.Empty;
    public string[] Args { get; private set; } = [];

    public bool BypassUpdateCheck => UninstallFlag.Active || WatcherFlag.Active;

    public LaunchSettings() { }

    public LaunchSettings(string[] args)
    {
        Args = args ?? [];
        ParseArgs(Args);
    }

    private void ParseArgs(string[] args)
    {
        if (args.Length == 0) return;

        var flagMap = new Dictionary<string, LaunchFlag>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in GetType().GetProperties())
        {
            if (prop.PropertyType == typeof(LaunchFlag) && prop.GetValue(this) is LaunchFlag flag)
            {
                foreach (var id in flag.Identifiers.Split(','))
                {
                    flagMap[id.Trim()] = flag;
                }
            }
        }

        int start = 0;
        string first = args[0];
        if (first.StartsWith("roblox:", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase))
        {
            RobloxLaunchMode = LaunchMode.Player;
            RobloxLaunchArgs = first;
            start = 1;
        }
        else if (first.StartsWith("roblox-studio-auth:", StringComparison.OrdinalIgnoreCase))
        {
            RobloxLaunchMode = LaunchMode.StudioAuth;
            RobloxLaunchArgs = first;
            start = 1;
        }
        else if (first.StartsWith("roblox-studio:", StringComparison.OrdinalIgnoreCase))
        {
            RobloxLaunchMode = LaunchMode.Studio;
            RobloxLaunchArgs = first;
            start = 1;
        }
        else if (first.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
        {
            VersionFlag.Active = true;
            VersionFlag.Data = first;
            start = 1;
        }

        for (int i = start; i < args.Length; i++)
        {
            string token = args[i];
            if (string.IsNullOrEmpty(token)) continue;

            if (!token.StartsWith('-'))
            {
                if ((token.StartsWith("roblox:", StringComparison.OrdinalIgnoreCase) ||
                     token.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase)) &&
                    RobloxLaunchMode == LaunchMode.None &&
                    string.IsNullOrEmpty(RobloxLaunchArgs))
                {
                    RobloxLaunchMode = LaunchMode.Player;
                    RobloxLaunchArgs = token;
                }
                else if (token.StartsWith("roblox-studio-auth:", StringComparison.OrdinalIgnoreCase) &&
                         RobloxLaunchMode == LaunchMode.None &&
                         string.IsNullOrEmpty(RobloxLaunchArgs))
                {
                    RobloxLaunchMode = LaunchMode.StudioAuth;
                    RobloxLaunchArgs = token;
                }
                else if (token.StartsWith("roblox-studio:", StringComparison.OrdinalIgnoreCase) &&
                         RobloxLaunchMode == LaunchMode.None &&
                         string.IsNullOrEmpty(RobloxLaunchArgs))
                {
                    RobloxLaunchMode = LaunchMode.Studio;
                    RobloxLaunchArgs = token;
                }
                else if (token.StartsWith("version-", StringComparison.OrdinalIgnoreCase) && !VersionFlag.Active)
                {
                    VersionFlag.Active = true;
                    VersionFlag.Data = token;
                }
                continue;
            }

            string flagName = token.TrimStart('-');
            if (flagMap.TryGetValue(flagName, out var targetFlag))
            {
                targetFlag.Active = true;
                bool expectsValue = ReferenceEquals(targetFlag, PlayerFlag) ||
                                    ReferenceEquals(targetFlag, StudioFlag) ||
                                    ReferenceEquals(targetFlag, VersionFlag) ||
                                    ReferenceEquals(targetFlag, ChannelFlag) ||
                                    ReferenceEquals(targetFlag, AccountFlag) ||
                                    ReferenceEquals(targetFlag, TargetFlag) ||
                                    ReferenceEquals(targetFlag, JobIdFlag);

                if (expectsValue && i < args.Length - 1 && !args[i + 1].StartsWith('-'))
                {
                    targetFlag.Data = args[++i];
                }
            }
        }

        if (PlayerFlag.Active)
        {
            RobloxLaunchMode = LaunchMode.Player;
            if (!string.IsNullOrEmpty(PlayerFlag.Data))
                RobloxLaunchArgs = PlayerFlag.Data;
        }
        else if (StudioFlag.Active)
        {
            RobloxLaunchMode = LaunchMode.Studio;
            if (!string.IsNullOrEmpty(StudioFlag.Data))
            {
                if (StudioFlag.Data.StartsWith("roblox-studio-auth:", StringComparison.OrdinalIgnoreCase))
                {
                    RobloxLaunchMode = LaunchMode.StudioAuth;
                    RobloxLaunchArgs = StudioFlag.Data;
                }
                else if (StudioFlag.Data.StartsWith("roblox-studio:", StringComparison.OrdinalIgnoreCase))
                {
                    RobloxLaunchArgs = StudioFlag.Data;
                }
                else
                {
                    RobloxLaunchArgs = $"-task EditFile -localPlaceFile \"{StudioFlag.Data}\"";
                }
            }
        }
    }
}
