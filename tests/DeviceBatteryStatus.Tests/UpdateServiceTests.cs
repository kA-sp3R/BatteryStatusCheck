using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeviceBatteryStatus.Core.Settings;
using DeviceBatteryStatus.Core.Updates;
using Xunit;

namespace DeviceBatteryStatus.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData(UpdateCheckMode.Off, false, UpdateCheckState.Off)]
    [InlineData(UpdateCheckMode.Off, true, UpdateCheckState.Off)]
    [InlineData(UpdateCheckMode.Manual, false, UpdateCheckState.ManualOnly)]
    [InlineData((UpdateCheckMode)255, true, UpdateCheckState.Off)]
    public async Task DisabledOrAutomaticManualModeNeverContactsTheNetwork(
        UpdateCheckMode mode, bool manual, UpdateCheckState expected)
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var result = await service.CheckAsync("1.0.0", mode, manual, CancellationToken.None);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Update);
        Assert.Empty(fixture.Requests);
        Assert.False(Directory.Exists(fixture.Staging));
    }

    [Theory]
    [InlineData("1.0.0", "1.1.0", UpdateCheckState.Available)]
    [InlineData("1.1.0", "1.1.0", UpdateCheckState.UpToDate)]
    [InlineData("2.0.0", "1.1.0", UpdateCheckState.UpToDate)]
    [InlineData("1.1.0-preview.1", "1.1.0", UpdateCheckState.Available)]
    [InlineData("1.1.0+local-build", "1.1.0", UpdateCheckState.UpToDate)]
    [InlineData("1.9.0", "1.10.0", UpdateCheckState.Available)]
    public async Task ChecksCompareStableVersionsWithoutDownloading(string current, string latest,
        UpdateCheckState expected)
    {
        using var fixture = new Fixture(latest);
        using var service = fixture.Service();
        var result = await service.CheckAsync(current, UpdateCheckMode.NotifyOnly, false, CancellationToken.None);
        Assert.Equal(expected, result.State);
        Assert.Single(fixture.Requests);
        Assert.EndsWith("/releases/latest", fixture.Requests[0]);
        Assert.False(Directory.Exists(fixture.Staging));
        if (expected == UpdateCheckState.Available)
        {
            Assert.Equal(latest, result.Update!.Version);
            Assert.Equal(fixture.InstallerName, result.Update.InstallerFileName);
            Assert.Equal(fixture.Installer.LongLength, result.Update.InstallerBytes);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.01.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0")]
    [InlineData("1.0.0-preview.01")]
    [InlineData("9999999999999999.0.0")]
    public async Task InvalidCurrentVersionCannotStartACheck(string current)
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var result = await service.CheckAsync(current, UpdateCheckMode.NotifyOnly, true, CancellationToken.None);
        Assert.Equal(UpdateCheckState.Unavailable, result.State);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task PrivateMissingOrRefusedFeedIsUnavailableWithoutCredentials(HttpStatusCode status)
    {
        using var fixture = new Fixture { MetadataStatus = status };
        using var service = fixture.Service();
        var result = await service.CheckAsync("1.0.0", UpdateCheckMode.Manual, true, CancellationToken.None);
        Assert.Equal(UpdateCheckState.Unavailable, result.State);
        Assert.Null(result.Update);
        Assert.Single(fixture.Requests);
        if (status == HttpStatusCode.NotFound) Assert.Contains("private", result.Detail);
        Assert.All(fixture.Headers, headers =>
        {
            Assert.Contains("DeviceBatteryStatus", headers);
            Assert.DoesNotContain("Authorization", headers, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cookie", headers, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("previewTag")]
    [InlineData("nonCanonicalTag")]
    [InlineData("wrongReleasePage")]
    [InlineData("missingAsset")]
    [InlineData("duplicateAsset")]
    [InlineData("missingDigest")]
    [InlineData("wrongDigestAlgorithm")]
    [InlineData("externalAssetUrl")]
    [InlineData("wrongAssetVersion")]
    [InlineData("assetNotUploaded")]
    [InlineData("invalidSize")]
    [InlineData("oversizeInstaller")]
    [InlineData("oversizeChecksum")]
    public async Task InvalidReleaseMetadataNeverAuthorizesADownload(string failure)
    {
        using var fixture = new Fixture();
        fixture.MutateMetadata(failure);
        using var service = fixture.Service();
        var result = await service.CheckAsync("1.0.0", UpdateCheckMode.NotifyOnly, true, CancellationToken.None);
        Assert.Equal(UpdateCheckState.Unavailable, result.State);
        Assert.Null(result.Update);
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedOrOversizedMetadataCannotAuthorizeADownload(bool oversized)
    {
        using var fixture = new Fixture();
        fixture.MetadataOverride = oversized ? new string('x', 256 * 1024 + 1) : "{not-json";
        using var service = fixture.Service();
        var result = await service.CheckAsync("1.0.0", UpdateCheckMode.NotifyOnly, true, CancellationToken.None);
        Assert.Equal(UpdateCheckState.Unavailable, result.State);
        Assert.Null(result.Update);
    }

    [Theory]
    [InlineData("http://github.com/kA-sp3R/BatteryStatusCheck/releases/latest")]
    [InlineData("https://evil.example/releases/latest")]
    [InlineData("https://api.github.com/repos/other/repository/releases/latest")]
    public async Task MetadataRedirectsOutsideTheExactFeedAreRejectedBeforeTheNextRequest(string location)
    {
        using var fixture = new Fixture { MetadataRedirect = location };
        using var service = fixture.Service();
        var result = await service.CheckAsync("1.0.0", UpdateCheckMode.NotifyOnly, true, CancellationToken.None);
        Assert.Equal(UpdateCheckState.Unavailable, result.State);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task AManualCheckCanBeRequestedWithoutChangingTheManualPreference()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var result = await service.CheckAsync("1.0.0", UpdateCheckMode.Manual, true, CancellationToken.None);
        Assert.Equal(UpdateCheckState.Available, result.State);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task DownloadIsSeparateAndMustAuthenticateChecksumAndExecutableBeforeIssuingAnInstallTicket()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service(trailingSeparator: true);
        var update = await fixture.Check(service);
        Assert.Single(fixture.Requests);

        var staged = await service.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.NotifyOnly, CancellationToken.None);

        Assert.Equal(new[] { fixture.MetadataUrl, fixture.ChecksumUrl, fixture.InstallerUrl }, fixture.Requests);
        Assert.Equal(update.Version, staged.Version);
        Assert.Equal(fixture.InstallerName, staged.FileName);
        using var verified = await service.VerifyForInstallAsync(staged, CancellationToken.None);
        Assert.True(Path.GetFullPath(verified.Path).StartsWith(fixture.Staging + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal(fixture.Installer, await File.ReadAllBytesAsync(verified.Path));
        Assert.Empty(Directory.GetFiles(fixture.Staging, "*.partial", SearchOption.AllDirectories));
        Assert.Equal(3, fixture.Requests.Count); // Local verification never contacts the network or executes the file.
    }

    [Fact]
    public async Task SwitchingOffRefusesDownloadEvenForAPreviouslyCheckedRelease()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var update = await fixture.Check(service);
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            service.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.Off, CancellationToken.None));
        Assert.Single(fixture.Requests);
        Assert.False(Directory.Exists(fixture.Staging));
    }

    [Fact]
    public async Task AnotherServiceCannotUseAReleaseOrInstallerTicket()
    {
        using var fixture = new Fixture();
        using var first = fixture.Service();
        using var second = fixture.Service();
        var update = await fixture.Check(first);
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            second.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.Manual, CancellationToken.None));
        var staged = await first.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.Manual, CancellationToken.None);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => second.VerifyForInstallAsync(staged, CancellationToken.None));
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Theory]
    [InlineData("checksumDigest")]
    [InlineData("checksumFileName")]
    [InlineData("checksumHash")]
    [InlineData("checksumExtraLine")]
    [InlineData("checksumTruncated")]
    [InlineData("installerDigest")]
    [InlineData("installerTruncated")]
    [InlineData("installerOversize")]
    public async Task CorruptOrMisassociatedDownloadsCannotLeaveAnExecutableTicket(string failure)
    {
        using var fixture = new Fixture();
        fixture.CorruptDownload(failure);
        using var service = fixture.Service();
        var update = await fixture.Check(service);

        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            service.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.Manual, CancellationToken.None));

        Assert.Empty(Directory.Exists(fixture.Staging)
            ? Directory.GetFiles(fixture.Staging, "*", SearchOption.AllDirectories) : []);
        Assert.Equal(failure.StartsWith("checksum", StringComparison.Ordinal) ? 2 : 3, fixture.Requests.Count);
    }

    [Theory]
    [InlineData("https://evil.example/download.exe")]
    [InlineData("http://release-assets.githubusercontent.com/download.exe")]
    [InlineData("https://github.com/another/repository/releases/download/v1.1.0/setup.exe")]
    [InlineData("https://release-assets.githubusercontent.com:8443/download.exe")]
    [InlineData("https://user@release-assets.githubusercontent.com/download.exe")]
    [InlineData("https://release-assets.githubusercontent.com/download.exe#fragment")]
    public async Task UntrustedDownloadRedirectsAreRejectedBeforeContactingTheTarget(string location)
    {
        using var fixture = new Fixture { InstallerRedirect = location };
        using var service = fixture.Service();
        var update = await fixture.Check(service);
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            service.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.Manual, CancellationToken.None));
        Assert.Equal(3, fixture.Requests.Count);
        Assert.DoesNotContain(location, fixture.Requests);
    }

    [Fact]
    public async Task AValidatedGitHubCdnRedirectCanProvideTheExactReleaseBytes()
    {
        using var fixture = new Fixture
        {
            InstallerRedirect = "https://release-assets.githubusercontent.com/github-production-release-asset/123/file?signature=fake"
        };
        using var service = fixture.Service();
        var staged = await service.DownloadVerifiedInstallerAsync(await fixture.Check(service), UpdateCheckMode.Manual, CancellationToken.None);
        using var verified = await service.VerifyForInstallAsync(staged, CancellationToken.None);
        Assert.Equal(fixture.Installer, await File.ReadAllBytesAsync(verified.Path));
        Assert.Equal(4, fixture.Requests.Count);
    }

    [Fact]
    public async Task RedirectLoopsHaveABoundedRequestCountAndNoRetainedDownload()
    {
        using var fixture = new Fixture { InstallerRedirectLoop = true };
        using var service = fixture.Service();
        var update = await fixture.Check(service);
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            service.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.Manual, CancellationToken.None));
        Assert.Equal(8, fixture.Requests.Count); // metadata, checksum, at most six executable/redirect requests.
        Assert.Empty(Directory.GetFiles(fixture.Staging, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TamperingAfterDownloadIsDetectedAgainAtTheSeparateInstallAction()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var staged = await service.DownloadVerifiedInstallerAsync(await fixture.Check(service), UpdateCheckMode.Manual, CancellationToken.None);
        var path = Assert.Single(Directory.GetFiles(fixture.Staging, "*.exe", SearchOption.AllDirectories));
        var altered = fixture.Installer.ToArray();
        altered[^1] ^= 0x80;
        await File.WriteAllBytesAsync(path, altered);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => service.VerifyForInstallAsync(staged, CancellationToken.None));
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Fact]
    public async Task AnInstallLeasePreventsConcurrentReplacementUntilItIsDisposed()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var staged = await service.DownloadVerifiedInstallerAsync(await fixture.Check(service), UpdateCheckMode.Manual, CancellationToken.None);
        using (var verified = await service.VerifyForInstallAsync(staged, CancellationToken.None))
        {
            Assert.Throws<IOException>(() => File.Open(verified.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        }
        var path = Assert.Single(Directory.GetFiles(fixture.Staging, "*.exe", SearchOption.AllDirectories));
        using var writable = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public async Task PostShutdownVerificationWorksWithoutTheDisposedHttpClient()
    {
        using var fixture = new Fixture();
        var service = fixture.Service();
        var staged = await service.DownloadVerifiedInstallerAsync(await fixture.Check(service), UpdateCheckMode.Manual, CancellationToken.None);
        service.Dispose();
        using var verified = await service.VerifyForInstallAsync(staged, CancellationToken.None);
        Assert.True(File.Exists(verified.Path));
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Fact]
    public async Task CancellationBeforeCheckDownloadOrInstallCannotStartMoreWork()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CheckAsync("1.0.0", UpdateCheckMode.NotifyOnly, true, cancellation.Token));
        Assert.Empty(fixture.Requests);
        var update = await fixture.Check(service);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.Manual, cancellation.Token));
        Assert.Single(fixture.Requests);
        var staged = await service.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.Manual, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.VerifyForInstallAsync(staged, cancellation.Token));
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Fact]
    public async Task CancellationDuringDownloadRemovesItsPartialDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture { InstallerRequestAction = cancellation.Cancel };
        using var service = fixture.Service();
        var update = await fixture.Check(service);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadVerifiedInstallerAsync(update, UpdateCheckMode.Manual, cancellation.Token));
        Assert.Empty(Directory.GetFiles(fixture.Staging, "*", SearchOption.AllDirectories));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "DeviceBatteryStatus-update-tests-" + Guid.NewGuid().ToString("N"));
        public string Version { get; }
        public string Staging => Path.Combine(directory, "staging");
        public string MetadataUrl => $"https://api.github.com/repos/{GitHubUpdateService.Repository}/releases/latest";
        public string InstallerName => $"DeviceBatteryStatus-{Version}-win-x64-setup.exe";
        public string InstallerUrl => $"https://github.com/{GitHubUpdateService.Repository}/releases/download/v{Version}/{InstallerName}";
        public string ChecksumUrl => Path.ChangeExtension(InstallerUrl, ".sha256");
        public byte[] Installer { get; } = Encoding.UTF8.GetBytes("MZ fake installer bytes used only for hashing; never executed.");
        public byte[] Checksum { get; private set; }
        public JsonObject Metadata { get; }
        public List<string> Requests { get; } = [];
        public List<string> Headers { get; } = [];
        public HttpStatusCode MetadataStatus { get; init; } = HttpStatusCode.OK;
        public string? MetadataOverride { get; set; }
        public string? MetadataRedirect { get; init; }
        public string? InstallerRedirect { get; init; }
        public bool InstallerRedirectLoop { get; init; }
        public Action? InstallerRequestAction { get; init; }
        private byte[]? checksumBody;
        private byte[]? installerBody;
        private bool unknownInstallerLength;

        public Fixture(string version = "1.1.0")
        {
            Version = version;
            Checksum = Encoding.UTF8.GetBytes(Hash(Installer) + "  " + InstallerName + "\n");
            Metadata = new JsonObject
            {
                ["tag_name"] = "v" + Version,
                ["draft"] = false,
                ["prerelease"] = false,
                ["html_url"] = $"https://github.com/{GitHubUpdateService.Repository}/releases/tag/v{Version}",
                ["assets"] = new JsonArray(Asset(InstallerName, InstallerUrl, Installer),
                    Asset(Path.ChangeExtension(InstallerName, ".sha256"), ChecksumUrl, Checksum))
            };
        }

        public GitHubUpdateService Service(bool trailingSeparator = false) =>
            new(Staging + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : ""), new Handler(this));

        public async Task<AvailableUpdate> Check(GitHubUpdateService service)
        {
            var result = await service.CheckAsync("1.0.0", UpdateCheckMode.NotifyOnly, true, CancellationToken.None);
            Assert.Equal(UpdateCheckState.Available, result.State);
            return Assert.IsType<AvailableUpdate>(result.Update);
        }

        public void MutateMetadata(string failure)
        {
            var assets = (JsonArray)Metadata["assets"]!;
            var installer = (JsonObject)assets[0]!;
            switch (failure)
            {
                case "draft": Metadata["draft"] = true; break;
                case "prerelease": Metadata["prerelease"] = true; break;
                case "previewTag": Metadata["tag_name"] = "v1.1.0-preview.1"; break;
                case "nonCanonicalTag": Metadata["tag_name"] = "v1.1.0+build"; break;
                case "wrongReleasePage": Metadata["html_url"] = "https://evil.example/release"; break;
                case "missingAsset": assets.RemoveAt(1); break;
                case "duplicateAsset": assets.Add(installer.DeepClone()); break;
                case "missingDigest": installer.Remove("digest"); break;
                case "wrongDigestAlgorithm": installer["digest"] = "sha1:" + new string('a', 40); break;
                case "externalAssetUrl": installer["browser_download_url"] = "https://evil.example/setup.exe"; break;
                case "wrongAssetVersion": installer["browser_download_url"] = InstallerUrl.Replace("v1.1.0", "v1.2.0"); break;
                case "assetNotUploaded": installer["state"] = "new"; break;
                case "invalidSize": installer["size"] = 0; break;
                case "oversizeInstaller": installer["size"] = 256L * 1024 * 1024 + 1; break;
                case "oversizeChecksum": assets[1]!["size"] = 4097; break;
                default: throw new InvalidOperationException(failure);
            }
        }

        public void CorruptDownload(string failure)
        {
            switch (failure)
            {
                case "checksumDigest": checksumBody = Encoding.UTF8.GetBytes("wrong checksum"); break;
                case "checksumFileName": SetAuthenticatedChecksum(Hash(Installer) + "  wrong.exe\n"); break;
                case "checksumHash": SetAuthenticatedChecksum(new string('0', 64) + "  " + InstallerName + "\n"); break;
                case "checksumExtraLine": SetAuthenticatedChecksum(Encoding.UTF8.GetString(Checksum) + "extra\n"); break;
                case "checksumTruncated": checksumBody = Checksum[..^1]; break;
                case "installerDigest": installerBody = Installer.ToArray(); installerBody[^1] ^= 1; break;
                case "installerTruncated": installerBody = Installer[..^1]; unknownInstallerLength = true; break;
                case "installerOversize": installerBody = [.. Installer, 0x01]; unknownInstallerLength = true; break;
                default: throw new InvalidOperationException(failure);
            }
        }

        private void SetAuthenticatedChecksum(string text)
        {
            Checksum = Encoding.UTF8.GetBytes(text);
            ((JsonArray)Metadata["assets"]!)[1] = Asset(Path.ChangeExtension(InstallerName, ".sha256"), ChecksumUrl, Checksum);
        }

        private static JsonObject Asset(string name, string url, byte[] bytes) => new()
        {
            ["name"] = name, ["browser_download_url"] = url, ["size"] = bytes.Length,
            ["state"] = "uploaded", ["digest"] = "sha256:" + Hash(bytes)
        };

        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        private HttpResponseMessage Respond(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            Headers.Add(request.Headers.ToString());
            if (request.RequestUri.AbsoluteUri == MetadataUrl)
            {
                if (MetadataRedirect is not null) return Redirect(MetadataRedirect);
                return new(MetadataStatus) { Content = new StringContent(MetadataOverride ?? Metadata.ToJsonString()) };
            }
            if (request.RequestUri.AbsoluteUri == ChecksumUrl)
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(checksumBody ?? Checksum) };
            if (request.RequestUri.AbsoluteUri == InstallerUrl || request.RequestUri.Host == "release-assets.githubusercontent.com")
            {
                if (InstallerRedirectLoop) return Redirect("https://release-assets.githubusercontent.com/loop");
                if (request.RequestUri.AbsoluteUri == InstallerUrl && InstallerRedirect is not null) return Redirect(InstallerRedirect);
                InstallerRequestAction?.Invoke();
                token.ThrowIfCancellationRequested();
                return new(HttpStatusCode.OK)
                {
                    Content = unknownInstallerLength ? new UnknownLengthContent(installerBody ?? Installer)
                        : new ByteArrayContent(installerBody ?? Installer)
                };
            }
            throw new InvalidOperationException("Unexpected fake update request: " + request.RequestUri.AbsoluteUri);
        }

        private static HttpResponseMessage Redirect(string uri) =>
            new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(uri) } };

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = fixture.Respond(request, cancellationToken);
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }

        private sealed class UnknownLengthContent(byte[] data) : HttpContent
        {
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        }
    }
}
