using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeviceBatteryStatus.Core.Settings;

namespace DeviceBatteryStatus.Core.Updates;

/// <summary>Notify-only stable-release checks and separately requested, verified installer staging. Never executes an installer.</summary>
public sealed class GitHubUpdateService : IDisposable
{
    public const string Repository = "kA-sp3R/BatteryStatusCheck";
    public static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases");
    private static readonly Uri LatestApi = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private const int MetadataLimit = 256 * 1024;
    private const int ChecksumLimit = 4096;
    private const long InstallerLimit = 256L * 1024 * 1024;
    private readonly HttpClient client;
    private readonly string stagingDirectory;
    private readonly object ticketGate = new();
    private readonly HashSet<AvailableUpdate> issuedUpdates = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<AvailableUpdate> updateOrder = new();
    private readonly HashSet<StagedInstaller> issuedInstallers = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim downloadGate = new(1, 1);
    private bool disposed;

    public GitHubUpdateService(string stagingDirectory, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        this.stagingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, UpdateCheckMode mode, bool manual,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(mode) || mode == UpdateCheckMode.Off)
            return new(UpdateCheckState.Off, null, "Update checks are off.");
        if (mode == UpdateCheckMode.Manual && !manual)
            return new(UpdateCheckState.ManualOnly, null, "Updates are checked only when you request it.");
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
            return new(UpdateCheckState.Unavailable, null, "The current application version cannot be compared safely.");
        ObjectDisposedException.ThrowIf(disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await GetAsync(LatestApi, LatestApi, metadata: true, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateCheckState.Unavailable, null,
                    "Release information is unavailable. This repository may be private; use the release page in your signed-in browser. No GitHub credentials are stored by the app.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new(UpdateCheckState.Unavailable, null, "The release service refused or rate-limited the request. Try again later.");
            Require(response.IsSuccessStatusCode, "The release service did not return release information.");
            byte[] bytes = await ReadBoundedAsync(response, MetadataLimit, null, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var release = document.RootElement;
            Require(release.ValueKind == JsonValueKind.Object && GetBool(release, "draft") is false &&
                GetBool(release, "prerelease") is false, "Only published stable releases are supported.");
            string tag = GetString(release, "tag_name");
            if (!tag.StartsWith('v') || !ReleaseVersion.TryParse(tag[1..], out var latest) || latest!.Prerelease)
                throw new UpdateVerificationException("The release has no supported stable version tag.");
            // Canonical stable tags exclude build suffixes and path-bearing/version-like strings.
            string version = $"{latest!.Major}.{latest.Minor}.{latest.Patch}";
            Require(tag == "v" + version, "The stable release tag is not canonical.");
            if (latest.CompareTo(current) <= 0)
                return new(UpdateCheckState.UpToDate, null, "No newer stable release was found.");
            var releasePage = new Uri($"https://github.com/{Repository}/releases/tag/{tag}");
            Require(GetString(release, "html_url") == releasePage.AbsoluteUri, "The release page does not match this repository and version.");
            Require(release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array,
                "The release has no asset list.");
            string installerName = $"DeviceBatteryStatus-{version}-win-x64-setup.exe";
            var installer = ParseAsset(assets, tag, installerName, InstallerLimit);
            var checksum = ParseAsset(assets, tag, Path.ChangeExtension(installerName, ".sha256"), ChecksumLimit);
            var update = new AvailableUpdate(version, releasePage, installer, checksum);
            lock (ticketGate)
            {
                issuedUpdates.Add(update);
                updateOrder.Enqueue(update);
                while (updateOrder.Count > 16) issuedUpdates.Remove(updateOrder.Dequeue());
            }
            return new(UpdateCheckState.Available, update,
                $"Version {version} is available. Downloading and installing require separate actions. The installer is unsigned.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(UpdateCheckState.Unavailable, null, "The update check timed out."); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new(UpdateCheckState.Unavailable, null, exception is UpdateVerificationException
                ? exception.Message : "Release information could not be retrieved or validated. No update was installed.");
        }
    }

    public async Task<StagedInstaller> DownloadVerifiedInstallerAsync(AvailableUpdate update, UpdateCheckMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        Require(Enum.IsDefined(mode) && mode != UpdateCheckMode.Off, "Update checks are off; the installer was not downloaded.");
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (ticketGate) Require(issuedUpdates.Contains(update), "This update was not issued by the current checking service. Check again.");
        await downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? ownedDirectory = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            // Fetch and authenticate the small checksum before downloading an executable.
            byte[] checksumBytes;
            using (var response = await GetAsync(update.Checksum.DownloadUri, update.Checksum.DownloadUri, false, timeout.Token).ConfigureAwait(false))
            {
                Require(response.IsSuccessStatusCode, "The checksum asset could not be downloaded.");
                checksumBytes = await ReadBoundedAsync(response, ChecksumLimit, update.Checksum.Bytes, timeout.Token).ConfigureAwait(false);
            }
            Require(Digest(checksumBytes) == update.Checksum.Sha256, "The checksum asset differs from GitHub's release digest.");
            string checksumText = new UTF8Encoding(false, true).GetString(checksumBytes);
            string expectedLine = update.Installer.Sha256 + "  " + update.Installer.Name;
            Require(checksumText.TrimEnd('\r', '\n') == expectedLine &&
                !checksumText.TrimEnd('\r', '\n').Contains('\n'), "The checksum does not match the selected installer and GitHub release digest.");
            Directory.CreateDirectory(stagingDirectory);
            RejectReparsePoint(stagingDirectory);
            // Bound retained staging to one previous successful download, plus this in-flight file.
            PruneOwnedStaging();
            ownedDirectory = Path.Combine(stagingDirectory, "installer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ownedDirectory);
            RejectReparsePoint(ownedDirectory);
            string partial = Path.Combine(ownedDirectory, "download.partial");
            string final = Path.Combine(ownedDirectory, update.Installer.Name);
            using (var response = await GetAsync(update.Installer.DownloadUri, update.Installer.DownloadUri, false, timeout.Token).ConfigureAwait(false))
            {
                Require(response.IsSuccessStatusCode, "The installer asset could not be downloaded.");
                ValidateLength(response, InstallerLimit, update.Installer.Bytes);
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536];
                long bytes = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    bytes += read;
                    Require(bytes <= update.Installer.Bytes && bytes <= InstallerLimit, "The installer exceeds its declared size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                Require(bytes == update.Installer.Bytes && Convert.ToHexStringLower(hash.GetHashAndReset()) == update.Installer.Sha256,
                    "The installer bytes do not match the release size and SHA-256 digest.");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, final);
            var staged = new StagedInstaller(update.Version, final, update.Installer.Sha256, update.Installer.Bytes);
            lock (ticketGate) issuedInstallers.Add(staged);
            ownedDirectory = null; // Successfully staged: retained for a separate Install action.
            return staged;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateVerificationException("The installer download timed out. No update was installed."); }
        catch (Exception exception) when (exception is HttpRequestException or DecoderFallbackException)
        { throw new UpdateVerificationException("The installer or its checksum could not be downloaded and validated."); }
        finally
        {
            if (ownedDirectory is not null) TryDeleteOwnedDirectory(ownedDirectory);
            downloadGate.Release();
        }
    }

    /// <summary>No network access. Works after HTTP disposal so an explicit post-shutdown installer launch can revalidate safely.</summary>
    public async Task<VerifiedInstaller> VerifyForInstallAsync(StagedInstaller staged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        cancellationToken.ThrowIfCancellationRequested();
        lock (ticketGate) Require(issuedInstallers.Contains(staged), "This staged installer was not issued by this application session.");
        string parent = Path.GetDirectoryName(staged.FullPath)!;
        Require(Path.GetDirectoryName(parent) == stagingDirectory && Path.GetFileName(parent).StartsWith("installer-", StringComparison.Ordinal) &&
            Path.GetFileName(staged.FullPath) == $"DeviceBatteryStatus-{staged.Version}-win-x64-setup.exe", "The staged installer path is invalid.");
        RejectReparsePoint(stagingDirectory);
        RejectReparsePoint(parent);
        RejectReparsePoint(staged.FullPath);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(staged.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            Require(stream.Length == staged.Bytes && Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) == staged.Sha256,
                "The staged installer changed after download. Download it again before installing.");
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            var verified = new VerifiedInstaller(staged.FullPath, stream);
            stream = null;
            return verified;
        }
        finally { stream?.Dispose(); }
    }

    private static UpdateAsset ParseAsset(JsonElement assets, string tag, string name, long limit)
    {
        var matches = assets.EnumerateArray().Where(asset => asset.ValueKind == JsonValueKind.Object &&
            asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == name).ToArray();
        Require(matches.Length == 1, "The release does not contain exactly one installer and checksum for this Windows package.");
        var asset = matches[0];
        if (GetString(asset, "state") != "uploaded" || !asset.TryGetProperty("size", out var size) ||
            !size.TryGetInt64(out long bytes) || bytes <= 0 || bytes > limit)
            throw new UpdateVerificationException("The release asset has an invalid state or size.");
        string digest = GetString(asset, "digest");
        Require(Regex.IsMatch(digest, @"\Asha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)),
            "The release lacks the GitHub SHA-256 digest required for verification.");
        var url = new Uri($"https://github.com/{Repository}/releases/download/{tag}/{name}");
        Require(GetString(asset, "browser_download_url") == url.AbsoluteUri, "The asset URL does not match this repository, tag and filename.");
        return new(name, url, bytes, digest[7..]);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, Uri assetOrigin, bool metadata, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            Require(IsAllowedUri(uri, assetOrigin, metadata), "An update request or redirect points outside the trusted GitHub release service.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DeviceBatteryStatus", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(metadata ? "application/vnd.github+json" : "application/octet-stream"));
            if (metadata) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != uri)
            {
                response.Dispose();
                throw new UpdateVerificationException("An unexpected automatic HTTP redirect was detected.");
            }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            Uri? location = response.Headers.Location;
            response.Dispose();
            Require(redirects < 5 && location is not null, "The release service returned too many or invalid redirects.");
            uri = location!.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new UpdateVerificationException("The release service returned too many redirects.");
    }

    private static bool IsAllowedUri(Uri uri, Uri origin, bool metadata)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return false;
        if (metadata) return uri == LatestApi;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return uri == origin;
        return uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, long? exact, CancellationToken cancellationToken)
    {
        ValidateLength(response, limit, exact);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            Require(buffer.Length + read <= limit && (exact is null || buffer.Length + read <= exact), "A release response exceeds its permitted size.");
            buffer.Write(bytes, 0, read);
        }
        Require(exact is null || buffer.Length == exact, "A release response does not match its declared size.");
        return buffer.ToArray();
    }

    private static void ValidateLength(HttpResponseMessage response, long limit, long? exact)
    {
        long? length = response.Content.Headers.ContentLength;
        Require(length is null || (length >= 0 && length <= limit && (exact is null || length == exact)), "The release response has an invalid content length.");
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new UpdateVerificationException("Required release metadata is missing.");
    private static bool? GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    { if (!condition) throw new UpdateVerificationException(message); }
    private static void RejectReparsePoint(string path)
    {
        Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "The staging path is a reparse point; download to a normal application staging folder.");
    }

    private void PruneOwnedStaging()
    {
        var directories = Directory.GetDirectories(stagingDirectory, "installer-*")
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"\Ainstaller-[0-9a-f]{32}\z", RegexOptions.CultureInvariant))
            .OrderByDescending(Directory.GetCreationTimeUtc).ToArray();
        foreach (string directory in directories.Skip(1)) TryDeleteOwnedDirectory(directory);
        Require(directories.Skip(1).All(directory => !Directory.Exists(directory)),
            "An older staged installer is still in use. Close it before downloading another update.");
        lock (ticketGate) issuedInstallers.RemoveWhere(ticket => !File.Exists(ticket.FullPath));
    }
    private static void TryDeleteOwnedDirectory(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Dispose();
        // Do not dispose the semaphore while an in-flight cancellation unwinds.
        // Local installer revalidation remains usable after the HTTP client closes.
    }
}
