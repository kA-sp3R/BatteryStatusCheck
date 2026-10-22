param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$OutputDirectory = 'artifacts/installer',
    [string]$CompilerPath,
    [string]$CompilerVersion,
    [switch]$CreateUpgradeFixture
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
trap {
    $message = "$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber)]"
    $escaped = $message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Output "::error title=Per-user installer build::$escaped"
    exit 1
}
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Compile the installer in Windows GitHub Actions.'
}
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
[xml]$project = Get-Content (Join-Path $repositoryRoot 'src/DeviceBatteryStatus.App/DeviceBatteryStatus.App.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid application version.' }
$numericVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$source = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $source -notmatch '^[0-9a-f]{40}$' -or $source -ne $env:GITHUB_SHA) {
    throw 'Installer source must match the workflow source.'
}
if (!$env:GITHUB_REPOSITORY -or $env:GITHUB_RUN_ID -notmatch '^\d+$' -or $env:GITHUB_RUN_ATTEMPT -notmatch '^\d+$') {
    throw 'Installer workflow provenance is required.'
}
$package = Get-Item -LiteralPath $PackagePath
$expectedName = "DeviceBatteryStatus-$version-win-x64-self-contained.zip"
if ($package.Name -cne $expectedName) { throw 'The installer must use this version of the self-contained application ZIP.' }
$packageHash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = [IO.Path]::ChangeExtension($package.FullName, '.sha256')
if ((Get-Content -LiteralPath $checksum -Raw).Trim() -cne "$packageHash  $expectedName") {
    throw 'Self-contained package checksum does not match.'
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output, ci-logs | Out-Null
$payload = Join-Path $output 'installer-payload'
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory $payload | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -match '(^/|\\|:|(^|/)\.\.(/|$))' -or $entry.FullName.EndsWith('/') -or
            $entry.Length -gt 512MB -or !$entries.TryAdd($entry.FullName, $entry)) {
            throw 'The application ZIP contains an unsafe or duplicate path.'
        }
    }
    foreach ($required in 'package-manifest.json', 'DeviceBatteryStatus.exe', 'coreclr.dll', 'README.md', 'THIRD_PARTY_NOTICES.md') {
        if (!$entries.ContainsKey($required)) { throw "The self-contained package lacks $required." }
    }
    $manifestStream = $entries['package-manifest.json'].Open()
    $reader = [IO.StreamReader]::new($manifestStream)
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
    finally { $reader.Dispose(); $manifestStream.Dispose() }
    if ($manifest.version -cne $version -or $manifest.sourceCommit -cne $source -or
        $manifest.repository -cne $env:GITHUB_REPOSITORY -or $manifest.runtimeIdentifier -ne 'win-x64' -or
        $manifest.workflowRun -cne $env:GITHUB_RUN_ID -or $manifest.workflowAttempt -cne $env:GITHUB_RUN_ATTEMPT -or
        $manifest.flavor -ne 'self-contained' -or $manifest.applicationLaunched -ne $false -or
        $manifest.hardwareVerified -ne $false -or $entries.Count -ne $manifest.files.Count + 1) {
        throw 'Self-contained package provenance is inconsistent.'
    }
    foreach ($relative in $manifest.files.Keys) {
        if (!$entries.ContainsKey($relative)) { throw "Package payload is missing: $relative" }
        $stream = $entries[$relative].Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($hash -cne $manifest.files[$relative]) { throw "Package payload checksum mismatch: $relative" }
    }
    [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $payload)
}
finally { $archive.Dispose() }

$appId = 'D38FB615-37DB-4AC0-92CE-BD1F947B9732'
$installation = [ordered]@{
    schemaVersion = 1; kind = 'per-user'; scope = 'currentUser'; appId = $appId
    version = $version; sourceCommit = $source; packageSha256 = $packageHash
}
[IO.File]::WriteAllText((Join-Path $payload 'installation.json'), ($installation | ConvertTo-Json) + "`n", [Text.UTF8Encoding]::new($false))
$ownedFiles = @((Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($payload, $_.FullName).Replace([char]92, [char]47)
}) + '.installed-files.txt' | Sort-Object -Unique)
[IO.File]::WriteAllLines((Join-Path $payload '.installed-files.txt'), $ownedFiles, [Text.UTF8Encoding]::new($false))

if (!$CompilerPath) {
    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $CompilerPath = $candidate; break }
    }
}
if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Inno Setup 6 ISCC.exe must be installed on the Windows runner; this script never downloads or executes an unverified compiler.'
}
$compiler = Get-Item -LiteralPath $CompilerPath
$observedCompilerVersion = "$($compiler.VersionInfo.FileMajorPart).$($compiler.VersionInfo.FileMinorPart).$($compiler.VersionInfo.FileBuildPart)"
if ($compiler.VersionInfo.FileMajorPart -ne 6 -or $compiler.VersionInfo.FileMinorPart -lt 3) {
    throw 'Inno Setup 6.3 or later in major version 6 is required for the installer architecture directives.'
}
if ($CompilerVersion -and $observedCompilerVersion -cne $CompilerVersion) {
    throw "The reviewed Inno Setup compiler version $CompilerVersion is required; runner provides $observedCompilerVersion. Review the runner/compiler change before updating -CompilerVersion."
}
$script = Join-Path $repositoryRoot 'installer/DeviceBatteryStatus.iss'
function Invoke-InstallerCompiler([string]$BuildVersion, [string]$BuildNumericVersion, [string]$BuildPayload, [string]$BuildOutput, [string]$LogName) {
    & $compiler.FullName "/DAppVersion=$BuildVersion" "/DAppVersionInfo=$BuildNumericVersion" "/DPayloadDirectory=$BuildPayload" "/DInstallerOutputDirectory=$BuildOutput" $script 2>&1 |
        Tee-Object -FilePath "ci-logs/$LogName.log"
    $code = $LASTEXITCODE
    Add-Content "ci-logs/$LogName.log" "`nnative_exit_code=$code"
    if ($code -ne 0) { throw "Inno Setup failed with native exit $code." }
}
Invoke-InstallerCompiler $version $numericVersion $payload $output 'installer-compile'
$installer = Join-Path $output "DeviceBatteryStatus-$version-win-x64-setup.exe"
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The installer compiler produced no expected executable.' }
$installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(([IO.Path]::ChangeExtension($installer, '.sha256')), "$installerHash  $([IO.Path]::GetFileName($installer))`n", [Text.UTF8Encoding]::new($false))
$receipt = [ordered]@{
    schemaVersion = 1; product = 'Device Battery Status'; version = $version; runtimeIdentifier = 'win-x64'
    distribution = 'per-user-installer'; signed = $false; sourceCommit = $source
    repository = $env:GITHUB_REPOSITORY; workflowRun = $env:GITHUB_RUN_ID; workflowAttempt = $env:GITHUB_RUN_ATTEMPT
    package = [ordered]@{ name = $package.Name; sha256 = $packageHash; bytes = $package.Length; workflowRun = $manifest.workflowRun; workflowAttempt = $manifest.workflowAttempt }
    installer = [ordered]@{ name = [IO.Path]::GetFileName($installer); sha256 = $installerHash; bytes = (Get-Item -LiteralPath $installer).Length }
    compiler = [ordered]@{ product = 'Inno Setup'; version = $observedCompilerVersion; sha256 = (Get-FileHash -LiteralPath $compiler.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    appId = $appId; applicationLaunched = $false; hardwareVerified = $false
}
[IO.File]::WriteAllText((Join-Path $output 'installer-release.json'), ($receipt | ConvertTo-Json -Depth 6) + "`n", [Text.UTF8Encoding]::new($false))

if ($CreateUpgradeFixture) {
    # This fixture tests installer version transitions and obsolete-file cleanup only;
    # its application payload is the same source, never a claimed old application build.
    $fixtureOutput = Join-Path $output 'upgrade-fixture'
    $fixturePayload = Join-Path $fixtureOutput 'payload'
    New-Item -ItemType Directory -Force $fixturePayload | Out-Null
    Get-ChildItem -LiteralPath $payload -Force | Copy-Item -Destination $fixturePayload -Recurse -Force
    $installation.version = '0.0.0-smoke-fixture'
    [IO.File]::WriteAllText((Join-Path $fixturePayload 'installation.json'), ($installation | ConvertTo-Json) + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $fixturePayload 'obsolete-upgrade-fixture.txt'), "Controlled prior-installer fixture only.`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllLines((Join-Path $fixturePayload '.installed-files.txt'), @($ownedFiles + 'obsolete-upgrade-fixture.txt'), [Text.UTF8Encoding]::new($false))
    Invoke-InstallerCompiler '0.0.0-smoke-fixture' '0.0.0.0' $fixturePayload $fixtureOutput 'installer-upgrade-fixture-compile'
    Remove-Item -LiteralPath $fixturePayload -Recurse -Force
}
Remove-Item -LiteralPath $payload -Recurse -Force
Write-Output "::notice title=Per-user installer built::$([IO.Path]::GetFileName($installer)); SHA256=$installerHash; Source=$source; Signed=false; ApplicationLaunched=false; HardwareVerified=false"
