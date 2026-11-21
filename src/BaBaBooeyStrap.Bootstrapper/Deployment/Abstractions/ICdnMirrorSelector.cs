namespace BaBaBooeyStrap.Bootstrapper.Deployment.Abstractions;

public interface ICdnMirrorSelector
{
    Task<string> SelectFastestMirrorAsync(
        IEnumerable<string> mirrorUrls,
        HttpClient? client = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    Task<string> ResolveOptimalCdnUrlAsync(
        CancellationToken ct = default);
}

public static class CdnMirrorSelectorDefaults
{
    public static readonly IReadOnlyList<string> GlobalMirrors = new[]
    {
        "https://setup.rbxcdn.com",
        "https://setup-cf.rbxcdn.com",
        "https://setup-ak.rbxcdn.com",
        "https://setup-aws.rbxcdn.com",
        "https://s3.amazonaws.com/setup.roblox.com"
    };

    public const string VersionStudioHash = "version-012732894899482c";
}
