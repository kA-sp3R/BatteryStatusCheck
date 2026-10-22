namespace DeviceBatteryStatus.Core.Updates;

public enum UpdateCheckState { Off, ManualOnly, UpToDate, Available, Unavailable }

public sealed record UpdateCheckResult(UpdateCheckState State, AvailableUpdate? Update, string Detail);

/// <summary>Immutable release metadata validated and issued by the checking service.</summary>
public sealed class AvailableUpdate
{
    public string Version { get; }
    public Uri ReleasePage { get; }
    public string InstallerFileName => Installer.Name;
    public long InstallerBytes => Installer.Bytes;
    internal UpdateAsset Installer { get; }
    internal UpdateAsset Checksum { get; }

    internal AvailableUpdate(string version, Uri releasePage, UpdateAsset installer, UpdateAsset checksum)
        => (Version, ReleasePage, Installer, Checksum) = (version, releasePage, installer, checksum);
}

internal sealed record UpdateAsset(string Name, Uri DownloadUri, long Bytes, string Sha256);

/// <summary>A service-issued installer receipt. A receipt alone is never permission to execute.</summary>
public sealed class StagedInstaller
{
    public string Version { get; }
    public string FileName { get; }
    internal string FullPath { get; }
    internal string Sha256 { get; }
    internal long Bytes { get; }
    internal StagedInstaller(string version, string fullPath, string sha256, long bytes)
        => (Version, FileName, FullPath, Sha256, Bytes) = (version, System.IO.Path.GetFileName(fullPath), fullPath, sha256, bytes);
}

/// <summary>Keep this lease until starting the deliberate installer process; it denies concurrent writes/deletion.</summary>
public sealed class VerifiedInstaller : IDisposable
{
    private readonly FileStream lease;
    public string Path { get; }
    internal VerifiedInstaller(string path, FileStream lease) => (Path, this.lease) = (path, lease);
    public void Dispose() => lease.Dispose();
}

public sealed class UpdateVerificationException(string message) : IOException(message);
