param(
    [Parameter(Mandatory)][string]$ApplicationPath,
    [string]$ReportDirectory = 'artifacts/application-smoke',
    [ValidateRange(10, 300)][int]$TimeoutSeconds = 120,
    [string]$ExpectedSource = $env:GITHUB_SHA,
    [string]$ExpectedVersion,
    [switch]$RequirePackageManifest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$app = (Resolve-Path -LiteralPath $ApplicationPath).Path
$appDirectory = Split-Path -Parent $app
$output = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$requiredArtifacts = @('popup.png', 'settings.png', 'settings-selected.png', 'updates-off.png',
    'tray-0-48.png', 'tray-100-48.png', 'tray-neutral-48.png', 'diagnostics-smoke.zip')
# Only this harness's fixed output names are removed. Prior evidence cannot make
# a later run pass; unrelated files in a caller-selected directory are preserved.
foreach ($name in @('application-smoke.json', 'stdout.log', 'stderr.log') + $requiredArtifacts) {
    $path = Join-Path $output $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
if ($ExpectedSource -and $ExpectedSource -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'ExpectedSource must be a complete source commit SHA.'
}
$manifest = $null
$manifestPath = Join-Path $appDirectory 'package-manifest.json'
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or
        $manifest.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
        $manifest.runtimeIdentifier -ne 'win-x64' -or
        $manifest.flavor -notin 'framework-dependent', 'self-contained') {
        throw 'Application package metadata is invalid.'
    }
    if ($ExpectedSource -and $manifest.sourceCommit -ne $ExpectedSource) { throw 'Package source does not match the smoke source.' }
    if ($env:GITHUB_REPOSITORY -and $manifest.repository -ne $env:GITHUB_REPOSITORY) { throw 'Package repository does not match this workflow.' }
    if ($env:GITHUB_RUN_ID -and $manifest.workflowRun -ne $env:GITHUB_RUN_ID) { throw 'Package build run does not match this workflow.' }
    if ($env:GITHUB_RUN_ATTEMPT -and $manifest.workflowAttempt -ne $env:GITHUB_RUN_ATTEMPT) { throw 'Package build attempt does not match this workflow.' }
    if (!$ExpectedVersion) { $ExpectedVersion = $manifest.version }
} elseif ($RequirePackageManifest) {
    throw 'Expand the inspected application ZIP before running this smoke; package-manifest.json is required.'
}

function Assert-PackagePayload {
    if ($null -eq $manifest) { return }
    $rootPrefix = [IO.Path]::GetFullPath($appDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($name in $manifest.files.Keys) {
        if ($name -match '(^/|\\|:|(^|/)\.\.(/|$))') { throw 'Unsafe package payload path.' }
        $path = [IO.Path]::GetFullPath((Join-Path $appDirectory $name))
        if (!$path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            !(Test-Path -LiteralPath $path -PathType Leaf) -or
            ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Package payload is missing or unsafe: $name"
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.files[$name]) {
            throw "Smoke input payload checksum differs from its inspected package: $name"
        }
    }
    foreach ($required in 'DeviceBatteryStatus.exe', 'DeviceBatteryStatus.dll', 'DeviceBatteryStatus.Core.dll', 'DeviceBatteryStatus.Windows.dll') {
        if (!$manifest.files.ContainsKey($required)) { throw "Package omitted the executable payload hash: $required" }
    }
    if ((Split-Path -Leaf $app) -cne 'DeviceBatteryStatus.exe') { throw 'Smoke must launch the inspected apphost.' }
}
Assert-PackagePayload
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $app
$start.WorkingDirectory = $appDirectory
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('--software-smoke-test')
$start.ArgumentList.Add($output)
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$started = $false
$stdout = $null
$stderr = $null
try {
    $started = $process.Start()
    if (!$started) { throw 'The isolated application smoke process did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        if (!$process.HasExited) { $process.Kill($true) }
        $process.WaitForExit()
        throw "Application smoke exceeded $TimeoutSeconds seconds; its own process tree was stopped."
    }
    $code = $process.ExitCode
    $reportPath = Join-Path $output 'application-smoke.json'
    if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "Application smoke produced no JSON report (exit $code)." }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    foreach ($check in $report.checks) {
        if (!$check.passed) {
            $message = "$($check.name): $($check.detail)".Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
            Write-Output "::error title=Windows application smoke::$message"
        }
    }
    if ($code -ne 0 -or $report.schemaVersion -ne 1 -or $report.mode -ne 'hardware-free-windows-application-smoke' -or
        $report.passed -ne $true -or @($report.checks).Count -ne 8 -or @($report.checks | Where-Object { !$_.passed }).Count -ne 0 -or
        @($report.checks.name | Select-Object -Unique).Count -ne 8 -or
        $report.counters.total -ne 8 -or $report.counters.executed -ne 8 -or $report.counters.passed -ne 8 -or $report.counters.failed -ne 0 -or
        $report.applicationLaunched -ne $true -or $report.normalApplicationControllerStarted -ne $false -or
        $report.hardwareAccessed -ne $false -or $report.networkAccessed -ne $false -or
        $report.startupRegistrationAccessed -ne $false -or $report.userSettingsPersisted -ne $false -or
        $report.runtimeIdentifier -ne 'win-x64') {
        throw "Isolated Windows application smoke did not pass; inspect application-smoke.json (exit $code)."
    }
    if ($ExpectedSource -and $report.sourceCommit -cne $ExpectedSource.ToLowerInvariant()) { throw 'Launched assembly source differs from the requested smoke source.' }
    if ($ExpectedVersion -and $report.appVersion -cne $ExpectedVersion) { throw 'Launched application version differs from the requested smoke version.' }
    if ($manifest) {
        foreach ($field in 'sourceCommit', 'flavor') {
            if ($report.$field -cne $manifest[$field]) { throw "Launched application $field differs from the package." }
        }
        if ($report.appVersion -cne $manifest.version) { throw 'Launched app version differs from its package.' }
        foreach ($field in 'repository', 'workflowRun', 'workflowAttempt') {
            if ($report.$field -and $report.$field -cne $manifest[$field]) { throw "Smoke $field differs from the package build." }
        }
    }
    if (@($report.artifacts).Count -ne $requiredArtifacts.Count -or
        @($report.artifacts | Where-Object { $_ -cnotin $requiredArtifacts }).Count -ne 0 -or
        @($report.artifacts | Select-Object -Unique).Count -ne $requiredArtifacts.Count) {
        throw 'Application smoke did not report the exact required artifact set.'
    }
    foreach ($name in $requiredArtifacts) {
        $path = Join-Path $output $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) {
            throw "Application smoke evidence is missing: $name"
        }
        if ($name.EndsWith('.png', [StringComparison]::Ordinal)) {
            $bytes = [IO.File]::ReadAllBytes($path)
            if ($bytes.Length -lt 8 -or [Convert]::ToHexString($bytes[0..7]) -cne '89504E470D0A1A0A') {
                throw "Application smoke image is invalid: $name"
            }
        }
    }
    Assert-PackagePayload # The smoke must not change the portable application files.
    $report | Add-Member -NotePropertyName packagePayloadVerified -NotePropertyValue ($null -ne $manifest) -Force
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "::notice title=Windows application smoke::Checks=8; Passed=8; AppVersion=$($report.appVersion); Source=$($report.sourceCommit); Flavor=$($report.flavor); PackagePayloadVerified=$($null -ne $manifest); ApplicationLaunched=true; NormalControllerStarted=false; HardwareAccessed=false; NetworkAccessed=false; StartupRegistrationAccessed=false; UserSettingsPersisted=false; InteractiveExplorerRestart=unverified; PhysicalMixedDpi=unverified"
} finally {
    if ($started) {
        if (!$process.HasExited) {
            try { $process.Kill($true); $process.WaitForExit() }
            catch [InvalidOperationException] { if (!$process.HasExited) { throw } }
        }
        # Preserve available failure output after killing a timed-out process as
        # well as on success. Never read Process.Id when process creation failed.
        if ($null -ne $stdout) { [IO.File]::WriteAllText((Join-Path $output 'stdout.log'), $stdout.GetAwaiter().GetResult()) }
        if ($null -ne $stderr) { [IO.File]::WriteAllText((Join-Path $output 'stderr.log'), $stderr.GetAwaiter().GetResult()) }
    }
    $process.Dispose()
}
