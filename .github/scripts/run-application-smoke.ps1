param(
    [Parameter(Mandatory)][string]$ApplicationPath,
    [Parameter(Mandatory)][string]$HarnessPath,
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
$harness = (Resolve-Path -LiteralPath $HarnessPath).Path
$harnessDirectory = Split-Path -Parent $harness
if ($harnessDirectory.Equals($appDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The separate harness must not be added to the original inspected application directory.'
}
$output = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$expectedChecks = 18
$requiredArtifacts = @('popup.png', 'settings.png', 'settings-selected.png', 'updates-off.png',
    'tray-0-48.png', 'tray-100-48.png', 'tray-neutral-48.png', 'diagnostics-smoke.zip',
    'capacity-details.png', 'capacity-unavailable.png', 'efficiency-workload.json',
    'runtime-popup.png', 'gaming-alert-settings.png', 'gaming-alert.png')
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
} else {
    throw 'Expand the inspected application ZIP before running the separate harness; package-manifest.json is required.'
}

function Assert-PackagePayload([string]$Directory) {
    $rootPrefix = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($name in $manifest.files.Keys) {
        if ($name -match '(^/|\\|:|(^|/)\.\.(/|$))') { throw 'Unsafe package payload path.' }
        $path = [IO.Path]::GetFullPath((Join-Path $Directory $name))
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
    if ((Split-Path -Leaf $app) -cne 'DeviceBatteryStatus.exe') { throw 'Smoke input must identify the inspected application apphost.' }
}
$harnessManifest = Get-Content -LiteralPath (Join-Path $harnessDirectory 'smoke-harness-manifest.json') -Raw | ConvertFrom-Json -AsHashtable
function Assert-HarnessPayload {
    if ((Split-Path -Leaf $harness) -cne 'DeviceBatteryStatus.Smoke.exe' -or
        $harnessManifest.schemaVersion -ne 1 -or $harnessManifest.mode -cne 'separate-synthetic-wpf-harness' -or
        $harnessManifest.sourceCommit -cne $manifest.sourceCommit -or $harnessManifest.targetVersion -cne $manifest.version -or
        $harnessManifest.targetFlavor -cne $manifest.flavor -or
        $harnessManifest.applicationManifestSha256 -cne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -or
        (Get-FileHash -LiteralPath (Join-Path $harnessDirectory 'package-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $harnessManifest.applicationManifestSha256) {
        throw 'Separate harness provenance differs from its inspected target package.'
    }
    $required = @('DeviceBatteryStatus.Smoke.exe', 'DeviceBatteryStatus.Smoke.dll',
        'DeviceBatteryStatus.Smoke.runtimeconfig.json', 'DeviceBatteryStatus.Smoke.deps.json')
    if ($harnessManifest.files.Count -ne $required.Count) { throw 'Separate harness payload inventory is invalid.' }
    foreach ($name in $required) {
        $path = Join-Path $harnessDirectory $name
        if (!$harnessManifest.files.ContainsKey($name) -or !(Test-Path -LiteralPath $path -PathType Leaf) -or
            ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $harnessManifest.files[$name]) {
            throw "Separate harness payload is missing or changed: $name"
        }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $harnessDirectory 'DeviceBatteryStatus.Smoke.runtimeconfig.json') -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath (Join-Path $appDirectory 'DeviceBatteryStatus.runtimeconfig.json') -Algorithm SHA256).Hash) {
        throw 'Separate harness runtime requirements differ from the actual application package.'
    }
}
Assert-PackagePayload $appDirectory
Assert-PackagePayload $harnessDirectory
Assert-HarnessPayload
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $harness
$start.WorkingDirectory = $harnessDirectory
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add($output)
if ($manifest.flavor -ceq 'self-contained') {
    # Deliberately point global runtime discovery at an empty directory. The
    # measured loaded coreclr/corelib paths must still belong to the package.
    $emptyRuntime = Join-Path $output 'empty-global-runtime'
    New-Item -ItemType Directory -Force $emptyRuntime | Out-Null
    $start.Environment['DOTNET_ROOT'] = $emptyRuntime
    $start.Environment['DOTNET_ROOT_X64'] = $emptyRuntime
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
}
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
    if ($code -ne 0 -or $report.schemaVersion -ne 2 -or $report.mode -ne 'hardware-free-windows-companion-smoke' -or
        $report.passed -ne $true -or @($report.checks).Count -ne $expectedChecks -or @($report.checks | Where-Object { !$_.passed }).Count -ne 0 -or
        @($report.checks.name | Select-Object -Unique).Count -ne $expectedChecks -or
        $report.counters.total -ne $expectedChecks -or $report.counters.executed -ne $expectedChecks -or $report.counters.passed -ne $expectedChecks -or $report.counters.failed -ne 0 -or
        $report.applicationLaunched -ne $false -or $report.harnessLaunched -ne $true -or $report.harnessExecuted -ne $true -or
        $report.packagedApplicationAssembliesLoaded -ne $true -or $report.normalApplicationControllerStarted -ne $false -or
        $report.hardwareAccessed -ne $false -or $report.networkAccessed -ne $false -or
        $report.startupRegistrationAccessed -ne $false -or $report.userSettingsPersisted -ne $false -or
        $report.runtimeIdentifier -ne 'win-x64') {
        throw "Isolated Windows application smoke did not pass; inspect application-smoke.json (exit $code)."
    }
    if ($report.harnessSourceCommit -cne $manifest.sourceCommit -or
        $report.harnessAssemblySha256 -cne $harnessManifest.files['DeviceBatteryStatus.Smoke.dll'] -or
        @($report.loadedApplicationAssemblies.PSObject.Properties).Count -ne 3 -or
        $report.runtimeHost.usesPackagedRuntime -ne ($manifest.flavor -ceq 'self-contained')) {
        throw 'Separate harness execution provenance or loaded runtime flavor differs from the package.'
    }
    foreach ($name in 'DeviceBatteryStatus.dll', 'DeviceBatteryStatus.Core.dll', 'DeviceBatteryStatus.Windows.dll') {
        if ($report.loadedApplicationAssemblies.$name -cne $manifest.files[$name]) {
            throw "Separate harness loaded different application assembly bytes: $name"
        }
    }
    if ($manifest.flavor -ceq 'self-contained' -and
        ($report.runtimeHost.coreClrSha256 -cne $manifest.files['coreclr.dll'] -or
        $report.runtimeHost.coreLibrarySha256 -cne $manifest.files['System.Private.CoreLib.dll'])) {
        throw 'Separate self-contained harness did not use the actual packaged runtime bytes.'
    }
    $efficiency = Get-Content -LiteralPath (Join-Path $output 'efficiency-workload.json') -Raw | ConvertFrom-Json
    if ($report.gaming.mode -cne 'synthetic-desktop-gaming-alert-smoke' -or
        $report.gaming.physicalDevicesAccessed -ne $false -or $report.gaming.gamesLaunched -ne $false -or
        $report.gaming.audioHardwareAccessed -ne $false -or
        $report.gaming.nativeSafety.noActivate -ne $true -or $report.gaming.nativeSafety.clickThrough -ne $true -or
        $report.gaming.nativeSafety.foregroundUnchanged -ne $true -or
        $report.gaming.nativeSafety.mouseActivateResult -ne 3 -or $report.gaming.nativeSafety.hitTestResult -ne -1 -or
        $report.gaming.repeatedEvents -ne 20 -or $report.gaming.coalescedDeliveries -ne 1 -or
        $report.gaming.previewAcknowledged -ne 0) {
        throw 'Synthetic gaming-alert evidence has invalid scope, native safety or delivery results.'
    }
    if ($efficiency.schemaVersion -ne 1 -or $efficiency.mode -cne 'synthetic-software-efficiency-smoke' -or
        $efficiency.sourceCommit -cne $report.sourceCommit -or $efficiency.appVersion -cne $report.appVersion -or
        $efficiency.flavor -cne $report.flavor -or $efficiency.physicalDevicesAccessed -ne $false -or
        $efficiency.normalApplicationControllerStarted -ne $false -or
        $efficiency.monitorWorkload.physicalDevicesAccessed -ne $false -or
        $efficiency.monitorWorkload.legacy.healthyReads -ne $efficiency.monitorWorkload.optimized.healthyReads -or
        $efficiency.monitorWorkload.optimized.discoveryEnumerations -ge $efficiency.monitorWorkload.legacy.discoveryEnumerations -or
        $efficiency.monitorWorkload.optimized.featureQueries -ge $efficiency.monitorWorkload.legacy.featureQueries) {
        throw 'Synthetic efficiency evidence has invalid provenance, scope or deterministic work counters.'
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
    Assert-PackagePayload $appDirectory # Original package files are never changed.
    Assert-PackagePayload $harnessDirectory # Staged target DLLs/runtime remain byte-identical too.
    Assert-HarnessPayload
    $report | Add-Member -NotePropertyName packagePayloadVerified -NotePropertyValue ($null -ne $manifest) -Force
    $report | Add-Member -NotePropertyName separateHarnessVerified -NotePropertyValue $true -Force
    $report | Add-Member -NotePropertyName harnessPayload -NotePropertyValue $harnessManifest -Force
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "::notice title=Windows companion smoke::Checks=$expectedChecks; Passed=$expectedChecks; AppVersion=$($report.appVersion); Source=$($report.sourceCommit); Flavor=$($report.flavor); PackagePayloadVerified=true; SeparateHarnessVerified=true; ApplicationLaunched=false; HarnessLaunched=true; PackagedAssembliesLoaded=true; NormalControllerStarted=false; HardwareAccessed=false; NetworkAccessed=false; StartupRegistrationAccessed=false; UserSettingsPersisted=false; EfficiencyWorkload=synthetic; InteractiveExplorerRestart=unverified; PhysicalMixedDpi=unverified"
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
