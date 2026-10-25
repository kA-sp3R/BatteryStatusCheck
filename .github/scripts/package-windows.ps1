param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [string]$OutputDirectory = 'artifacts/packages',
    [Parameter(Mandatory)][ValidateSet('framework-dependent', 'self-contained')][string]$Flavor
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
trap {
    $message = "$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber)]"
    $escaped = $message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Output "::error title=Windows packaging::$escaped"
    exit 1
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
[xml]$project = Get-Content (Join-Path $repositoryRoot 'src/DeviceBatteryStatus.App/DeviceBatteryStatus.App.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid package version.' }
$source = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $source -notmatch '^[0-9a-f]{40}$' -or $source -ne $env:GITHUB_SHA) {
    throw 'The checked-out source does not match the workflow commit.'
}
if (!$env:GITHUB_REPOSITORY -or !$env:GITHUB_SERVER_URL -or $env:GITHUB_RUN_ID -notmatch '^\d+$' -or $env:GITHUB_RUN_ATTEMPT -notmatch '^\d+$') {
    throw 'Workflow provenance is required.'
}
$publish = (Resolve-Path $PublishDirectory).Path
foreach ($required in 'DeviceBatteryStatus.exe', 'DeviceBatteryStatus.dll', 'DeviceBatteryStatus.deps.json',
    'DeviceBatteryStatus.runtimeconfig.json', 'DeviceBatteryStatus.Core.dll', 'DeviceBatteryStatus.Windows.dll') {
    if (!(Test-Path (Join-Path $publish $required) -PathType Leaf)) { throw "Missing published file: $required" }
}
$runtimeConfig = Get-Content (Join-Path $publish 'DeviceBatteryStatus.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
$selfContained = $Flavor -eq 'self-contained'
if ($selfContained -and !(Test-Path (Join-Path $publish 'coreclr.dll') -PathType Leaf)) { throw 'Self-contained runtime is missing.' }
if (!$selfContained -and (Test-Path (Join-Path $publish 'coreclr.dll'))) { throw 'Framework-dependent output unexpectedly contains a runtime.' }

$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$packageName = "DeviceBatteryStatus-$version-win-x64-$Flavor"
$staging = Join-Path $output $packageName
if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory $staging | Out-Null
Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $staging -Recurse -Force
foreach ($file in Get-ChildItem -LiteralPath $staging -Recurse -File) {
    if ($file.Name -match '\.(pdb|cs|csproj)$' -or
        $file.Name -match '^DeviceBatteryStatus\.(Smoke|Tests)(\.|$)' -or
        $file.Name -in 'application-smoke.json', 'smoke-harness-manifest.json', 'efficiency-workload.json') {
        throw "Development or verification files must be kept outside the application package: $($file.Name)"
    }
}
Copy-Item (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') (Join-Path $staging 'THIRD_PARTY_NOTICES.md') -Force
Copy-Item (Join-Path $repositoryRoot 'docs/release.md') (Join-Path $staging 'README.md') -Force

$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot record the packaging SDK.' }
# Both flavors redistribute an apphost from the installed .NET distribution.
# Keep that distribution's notices as well as exact runtime-pack notices below.
$dotnetRoot = Split-Path (Get-Command dotnet -CommandType Application).Source -Parent
$distributionNotices = @(Get-ChildItem -LiteralPath $dotnetRoot -File | Where-Object {
    $_.Name -match '^(LICENSE(?:\.(?:TXT|MD))?|THIRD[-_]?PARTY[-_]?NOTICES\.(?:TXT|MD))$'
})
if (@($distributionNotices | Where-Object { $_.Name -match '^LICENSE(?:\.|$)' }).Count -eq 0 -or
    @($distributionNotices | Where-Object { $_.Name -match '^THIRD[-_]?PARTY[-_]?NOTICES\.' }).Count -eq 0) {
    throw 'The active .NET distribution is missing license/notice text for apphost redistribution.'
}
$distributionNoticePaths = @()
foreach ($notice in $distributionNotices) {
    $destination = "licenses/dotnet-distribution/$($notice.Name)"
    $destinationPath = Join-Path $staging $destination
    New-Item -ItemType Directory -Force (Split-Path $destinationPath -Parent) | Out-Null
    Copy-Item -LiteralPath $notice.FullName -Destination $destinationPath
    $distributionNoticePaths += $destination
}

$runtimeLicenses = @()
if ($selfContained) {
    if (!$env:NUGET_PACKAGES) { throw 'The runtime package cache must be explicit for license provenance.' }
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $pack = "$($framework.name).Runtime.win-x64"
        $packDirectory = Join-Path $env:NUGET_PACKAGES "$($pack.ToLowerInvariant())/$($framework.version)"
        if (!(Test-Path $packDirectory -PathType Container)) { throw "Cannot locate redistributed runtime pack: $pack $($framework.version)" }
        $notices = @(Get-ChildItem -LiteralPath $packDirectory -Recurse -File | Where-Object {
            $_.Name -match '^(LICENSE(?:\.(?:TXT|MD))?|THIRD[-_]?PARTY[-_]?NOTICES\.(?:TXT|MD))$'
        })
        if (@($notices | Where-Object { $_.Name -match '^LICENSE(?:\.|$)' }).Count -eq 0) {
            $inventory = @(Get-ChildItem -LiteralPath $packDirectory -Force | Select-Object -First 20 -ExpandProperty Name) -join ', '
            throw "Runtime pack is missing license text: $pack $($framework.version); Root inventory: $inventory"
        }
        $licensePaths = @()
        foreach ($notice in $notices) {
            $relative = [IO.Path]::GetRelativePath($packDirectory, $notice.FullName).Replace([char]92, [char]47)
            $destination = "licenses/$pack-$($framework.version)/$relative"
            $destinationPath = Join-Path $staging $destination
            New-Item -ItemType Directory -Force (Split-Path $destinationPath -Parent) | Out-Null
            Copy-Item -LiteralPath $notice.FullName -Destination $destinationPath
            $licensePaths += $destination
        }
        $thirdPartyPaths = @($licensePaths | Where-Object { [IO.Path]::GetFileName($_) -match '^THIRD[-_]?PARTY[-_]?NOTICES\.' })
        $thirdPartySource = 'runtime-pack'
        if ($thirdPartyPaths.Count -eq 0) {
            # Some Windows Desktop packs omit this text. Retain the actual pack's
            # license and use the separately identified distribution-wide notices.
            $thirdPartySource = 'active-dotnet-distribution'
            $thirdPartyPaths = @($distributionNoticePaths | Where-Object { [IO.Path]::GetFileName($_) -match '^THIRD[-_]?PARTY[-_]?NOTICES\.' })
        }
        $runtimeLicenses += [ordered]@{
            pack = $pack; version = $framework.version; files = $licensePaths
            thirdPartyNoticeSource = $thirdPartySource; thirdPartyNoticeFiles = $thirdPartyPaths
        }
        Write-Output "::notice title=Runtime notice provenance::$pack $($framework.version); LicenseSource=runtime-pack; ThirdPartyNoticeSource=$thirdPartySource"
    }
}

$files = [ordered]@{}
$stagingFiles = @(Get-ChildItem -LiteralPath $staging -Recurse -File | Sort-Object FullName)
foreach ($file in $stagingFiles) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package input contains a reparse point.' }
    $relative = [IO.Path]::GetRelativePath($staging, $file.FullName).Replace([char]92, [char]47)
    if ($relative -in 'package-manifest.json', 'SHA256SUMS.txt') { throw 'Publish output contains reserved package metadata.' }
    $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'Device Battery Status'
    version = $version
    runtimeIdentifier = 'win-x64'
    flavor = $Flavor
    sourceCommit = $source
    repository = $env:GITHUB_REPOSITORY
    workflowRun = $env:GITHUB_RUN_ID
    workflowAttempt = $env:GITHUB_RUN_ATTEMPT
    workflowUrl = "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)"
    sdkVersion = $sdk
    runtimeOptions = $runtimeConfig.runtimeOptions
    archiveCompression = 'Deflate/SmallestSize'
    applicationLaunched = $false
    hardwareVerified = $false
    distributionNotices = [ordered]@{ source = 'active-dotnet-distribution'; sdkVersion = $sdk; files = $distributionNoticePaths }
    runtimeLicenses = @($runtimeLicenses)
    files = $files
}
[IO.File]::WriteAllText((Join-Path $staging 'package-manifest.json'), ($manifest | ConvertTo-Json -Depth 12) + "`n", [Text.UTF8Encoding]::new($false))

# Stable entry order and a source-derived timestamp avoid filesystem-order/time noise.
# Run provenance intentionally makes packages from different workflow runs distinct.
$commitTimestamp = (& git -C $repositoryRoot show -s --format=%cI HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read source timestamp.' }
$timestamp = [DateTimeOffset]::Parse($commitTimestamp).ToUniversalTime()
if ($timestamp.Year -lt 1980 -or $timestamp.Year -gt 2107) { throw 'Source timestamp is outside the ZIP timestamp range.' }
$zipPath = Join-Path $output "$packageName.zip"
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $staging -Recurse -File | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($staging, $file.FullName).Replace([char]92, [char]47)
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::SmallestSize)
        $entry.LastWriteTime = $timestamp
        $inputStream = [IO.File]::OpenRead($file.FullName)
        try {
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output "$packageName.sha256"), "$hash  $packageName.zip`n", [Text.UTF8Encoding]::new($false))
Remove-Item -LiteralPath $staging -Recurse -Force
Write-Output "::notice title=Windows package created::$packageName.zip; Bytes=$((Get-Item $zipPath).Length); SHA256=$hash; Source=$source"
