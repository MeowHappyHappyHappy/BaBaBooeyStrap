namespace BaBaBooeyStrap.Bootstrapper.Deployment.Models;

public record PackageDownloadProgress(
    string CurrentPackage,
    long DownloadedBytes,
    long TotalBytes,
    double ProgressPercentage,
    int CompletedPackages,
    int TotalPackages
);
