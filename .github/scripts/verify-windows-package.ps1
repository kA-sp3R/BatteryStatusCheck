param(
    [string]$PackageDirectory = 'artifacts/packages',
    [Parameter(Mandatory)][ValidateSet('framework-dependent', 'self-contained')][string]$Flavor
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
trap {
    $message = "$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber)]"
    $escaped = $message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Output "::error title=Windows package inspection::$escaped"
    exit 1
}

$archives = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter "*-$Flavor.zip" -File)
if ($archives.Count -ne 1) { throw "Expected exactly one $Flavor package." }
$zip = $archives[0]
$expectedHash = (Get-Content -LiteralPath ([IO.Path]::ChangeExtension($zip.FullName, '.sha256')) -Raw).Trim()
$actualHash = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expectedHash -cne "$actualHash  $($zip.Name)") { throw 'Package archive checksum does not match.' }

function Read-EntryBytes([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); return ,$memory.ToArray() }
    finally { $stream.Dispose(); $memory.Dispose() }
}

$archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
try {
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -match '(^/|\\|:|(^|/)\.\.(/|$))' -or $entry.FullName.EndsWith('/') -or
            !$entries.TryAdd($entry.FullName, $entry)) { throw "Unsafe or duplicate ZIP entry: $($entry.FullName)" }
        if ($entry.Length -gt 512MB) { throw "Unexpectedly large package entry: $($entry.FullName)" }
    }
    foreach ($required in 'DeviceBatteryStatus.exe', 'DeviceBatteryStatus.dll', 'DeviceBatteryStatus.deps.json',
        'DeviceBatteryStatus.runtimeconfig.json', 'DeviceBatteryStatus.Core.dll', 'DeviceBatteryStatus.Windows.dll',
        'THIRD_PARTY_NOTICES.md', 'README.md', 'package-manifest.json') {
        if (!$entries.ContainsKey($required)) { throw "Package is missing $required" }
    }
    $manifestText = [Text.Encoding]::UTF8.GetString((Read-EntryBytes $entries['package-manifest.json']))
    $manifest = $manifestText | ConvertFrom-Json -AsHashtable
    [xml]$project = Get-Content (Join-Path $PSScriptRoot '../../src/DeviceBatteryStatus.App/DeviceBatteryStatus.App.csproj') -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'Device Battery Status' -or $manifest.version -cne $version -or
        $zip.Name -cne "DeviceBatteryStatus-$version-win-x64-$Flavor.zip" -or
        $manifest.flavor -ne $Flavor -or $manifest.runtimeIdentifier -ne 'win-x64' -or
        $manifest.sourceCommit -cne $env:GITHUB_SHA -or $manifest.repository -cne $env:GITHUB_REPOSITORY -or
        $manifest.workflowRun -cne $env:GITHUB_RUN_ID -or $manifest.workflowAttempt -cne $env:GITHUB_RUN_ATTEMPT -or
        $manifest.workflowUrl -cne "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)" -or
        $manifest.applicationLaunched -ne $false -or $manifest.hardwareVerified -ne $false) {
        throw 'Package provenance does not match this workflow and source.'
    }
    if ($entries.Count -ne $manifest.files.Count + 1) { throw 'Package manifest does not enumerate the exact payload.' }
    foreach ($path in $manifest.files.Keys) {
        if (!$entries.ContainsKey($path)) { throw "Manifest payload is missing: $path" }
        $stream = $entries[$path].Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($hash -cne $manifest.files[$path]) { throw "Payload checksum mismatch: $path" }
    }
    if ($manifest.distributionNotices.source -ne 'active-dotnet-distribution' -or
        $manifest.distributionNotices.sdkVersion -cne $manifest.sdkVersion) { throw 'Missing apphost notice provenance.' }
    $distributionPaths = @($manifest.distributionNotices.files)
    if (@($distributionPaths | Where-Object { [IO.Path]::GetFileName($_) -match '^LICENSE(?:\.|$)' }).Count -eq 0 -or
        @($distributionPaths | Where-Object { [IO.Path]::GetFileName($_) -match '^THIRD[-_]?PARTY[-_]?NOTICES\.' }).Count -eq 0) {
        throw 'Missing .NET distribution license/notice files for apphost redistribution.'
    }
    foreach ($path in $distributionPaths) {
        if (!$path.StartsWith('licenses/dotnet-distribution/', [StringComparison]::Ordinal) -or
            !$entries.ContainsKey($path) -or $entries[$path].Length -eq 0) { throw "Invalid .NET distribution notice payload: $path" }
    }
    $exe = Read-EntryBytes $entries['DeviceBatteryStatus.exe']
    if ($exe.Length -lt 256 -or $exe[0] -ne 0x4D -or $exe[1] -ne 0x5A) { throw 'Apphost is not a PE executable.' }
    $header = [BitConverter]::ToInt32($exe, 0x3C)
    if ($header -lt 0 -or $header + 96 -gt $exe.Length -or [BitConverter]::ToUInt32($exe, $header) -ne 0x00004550 -or
        [BitConverter]::ToUInt16($exe, $header + 4) -ne 0x8664 -or
        [BitConverter]::ToUInt16($exe, $header + 24) -ne 0x020B -or
        [BitConverter]::ToUInt16($exe, $header + 24 + 68) -ne 2) {
        throw 'Apphost must be a win-x64 PE32+ Windows GUI executable.'
    }
    $selfContained = $Flavor -eq 'self-contained'
    if ($entries.ContainsKey('coreclr.dll') -ne $selfContained) { throw 'Runtime payload does not match the package flavor.' }
    if ($selfContained) {
        foreach ($required in 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
            'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll', 'System.Windows.Forms.dll') {
            if (!$entries.ContainsKey($required) -or $entries[$required].Length -eq 0) { throw "Missing bundled runtime payload: $required" }
        }
    }
    $config = [Text.Encoding]::UTF8.GetString((Read-EntryBytes $entries['DeviceBatteryStatus.runtimeconfig.json'])) | ConvertFrom-Json -AsHashtable
    $options = $config.runtimeOptions
    if ($options.tfm -ne 'net10.0') { throw 'Unexpected target framework.' }
    $frameworks = if ($selfContained) { @($options.includedFrameworks) } else { @($options.frameworks) }
    if (@($frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -match '^10\.' }).Count -ne 1) {
        throw 'Windows Desktop Runtime 10 dependency or bundled framework is missing.'
    }
    if ($selfContained) {
        foreach ($framework in $frameworks) {
            $pack = "$($framework.name).Runtime.win-x64"
            $licenses = @($manifest.runtimeLicenses | Where-Object { $_.pack -eq $pack -and $_.version -eq $framework.version })
            if ($licenses.Count -ne 1) { throw "Missing runtime license provenance: $pack" }
            $paths = @($licenses[0].files)
            if (@($paths | Where-Object { [IO.Path]::GetFileName($_) -match '^LICENSE(?:\.|$)' }).Count -eq 0 -or
                @($paths | Where-Object { [IO.Path]::GetFileName($_) -match '^THIRD[-_]?PARTY[-_]?NOTICES\.' }).Count -eq 0) {
                throw "Missing runtime license/notice files: $pack"
            }
            foreach ($path in $paths) {
                if (!$path.StartsWith("licenses/$pack-$($framework.version)/", [StringComparison]::Ordinal) -or
                    !$entries.ContainsKey($path) -or $entries[$path].Length -eq 0) { throw "Invalid runtime license payload: $path" }
            }
        }
    }
    Write-Output "::notice title=Windows package inspection::$($zip.Name); Files=$($entries.Count); PE=win-x64/GUI; PayloadHashes=verified; SHA256=$actualHash; ApplicationLaunched=false; HardwareVerified=false"
} finally { $archive.Dispose() }
