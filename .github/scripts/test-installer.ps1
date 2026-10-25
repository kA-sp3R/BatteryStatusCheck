param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$UpgradeFixturePath,
    [string]$ReportDirectory = 'artifacts/installer-smoke'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Run installer smoke checks only in an isolated Windows GitHub Actions job.'
}

$installer = Get-Item -LiteralPath $InstallerPath
if (!$UpgradeFixturePath) {
    $UpgradeFixturePath = Join-Path $installer.DirectoryName 'upgrade-fixture/DeviceBatteryStatus-0.0.0-smoke-fixture-win-x64-setup.exe'
}
$fixture = Get-Item -LiteralPath $UpgradeFixturePath
$receipt = Get-Content -LiteralPath (Join-Path $installer.DirectoryName 'installer-release.json') -Raw | ConvertFrom-Json
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Force $reportRoot | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$processResults = [Collections.Generic.List[object]]::new()
$instance = [Guid]::NewGuid().ToString('N')
$installDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Programs/DeviceBatteryStatus-Smoke-$instance"
# DisableProgramGroupPage=yes intentionally fixes the production Start menu
# group. Inno ignores /GROUP in that mode, so verify the actual default group
# in this fresh isolated profile rather than requesting an unsupported override.
$groupName = 'Device Battery Status'
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) $groupName
$dataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DeviceBatteryStatus'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D38FB615-37DB-4AC0-92CE-BD1F947B9732}_is1'
$startupKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$startupValue = 'DeviceBatteryStatus'
$ownedCommand = '"' + (Join-Path $installDirectory 'DeviceBatteryStatus.exe') + '" --startup'
$foreignCommand = '"C:\DeviceBatteryStatus-Smoke-Portable\DeviceBatteryStatus.exe" --startup'
$completed = $false
$failure = $null
$dataCreated = $false
$startupTouched = $false
$installerExecuted = $false
$ownedPayloadNames = @()
$initialInstallPassed = $false
$upgradePassed = $false
$ownedStartupUninstallPassed = $false
$foreignStartupUninstallPassed = $false

function Assert-InstallerCheck([bool]$Condition, [string]$Description) {
    if (!$Condition) { throw $Description }
    if ($checks.Contains($Description)) { throw "Ambiguous repeated installer smoke check: $Description" }
    $checks.Add($Description)
}
function Read-UninstallValue([string]$Name) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKey)
    if ($null -eq $key) { return $null }
    try { return $key.GetValue($Name) }
    finally { $key.Dispose() }
}
function Read-StartupValue {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($startupKey)
    if ($null -eq $key) { return $null }
    try { return $key.GetValue($startupValue, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Dispose() }
}
function Set-SmokeStartupValue([string]$Command) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($startupKey, $true)
    try {
        $key.SetValue($startupValue, $Command, [Microsoft.Win32.RegistryValueKind]::String)
        $script:startupTouched = $true
    }
    finally { $key.Dispose() }
}
function Invoke-SmokeInstaller([string]$Executable, [string]$Label, [bool]$ExpectSuccess = $true, [switch]$Uninstall) {
    foreach ($path in @($Executable, $installDirectory, $reportRoot, $groupName)) {
        if ($path.Contains('"') -or $path -match '[\r\n]') { throw 'Unsafe installer smoke argument.' }
    }
    $logPath = Join-Path $reportRoot "$Label.log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$logPath`"")
    if (!$Uninstall) { $arguments += @('/SP-', "/DIR=`"$installDirectory`"") }
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru
    $script:installerExecuted = $true
    try {
        if (!$process.WaitForExit(120000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Installer operation $Label exceeded its 120-second deadline."
        }
        $code = $process.ExitCode
        $processResults.Add([ordered]@{ operation = $Label; exitCode = $code; expectedSuccess = $ExpectSuccess })
        if ($ExpectSuccess -and $code -ne 0) { throw "Installer operation $Label failed with exit $code; see its retained setup log." }
        if (!$ExpectSuccess -and $code -eq 0) { throw "Installer operation $Label unexpectedly succeeded while the app mutex was held." }
    }
    finally { $process.Dispose() }
}
function Assert-SettingsPreserved([string]$Stage) {
    Assert-InstallerCheck ((Get-FileHash -LiteralPath (Join-Path $dataDirectory 'settings.json') -Algorithm SHA256).Hash -ceq $script:settingsHash) "Settings bytes preserved after $Stage"
    Assert-InstallerCheck ((Get-FileHash -LiteralPath (Join-Path $dataDirectory 'settings.json.bak') -Algorithm SHA256).Hash -ceq $script:backupHash) "Settings backup preserved after $Stage"
    Assert-InstallerCheck ((Get-FileHash -LiteralPath (Join-Path $dataDirectory 'logs/smoke-preservation.jsonl') -Algorithm SHA256).Hash -ceq $script:logHash) "Diagnostic log preserved after $Stage"
    Assert-InstallerCheck ((Get-FileHash -LiteralPath (Join-Path $dataDirectory 'capacity-health.json') -Algorithm SHA256).Hash -ceq $script:healthHash) "Capacity history bytes preserved after $Stage"
    Assert-InstallerCheck ((Get-FileHash -LiteralPath (Join-Path $dataDirectory 'capacity-health.json.bak') -Algorithm SHA256).Hash -ceq $script:healthBackupHash) "Capacity history backup preserved after $Stage"
}
function Assert-InstalledPayload([string]$ExpectedVersion, [string]$Stage) {
    $marker = Get-Content -LiteralPath (Join-Path $installDirectory 'installation.json') -Raw | ConvertFrom-Json
    Assert-InstallerCheck ($marker.kind -ceq 'per-user' -and $marker.scope -ceq 'currentUser' -and $marker.version -ceq $ExpectedVersion -and $marker.sourceCommit -ceq $receipt.sourceCommit) "Per-user installation marker and source match $ExpectedVersion during $Stage"
    $manifest = Get-Content -LiteralPath (Join-Path $installDirectory 'package-manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in $manifest.files.Keys) {
        $path = Join-Path $installDirectory $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.files[$name]) {
            throw "Installed application payload differs from its source-bound manifest: $name"
        }
    }
    Assert-InstallerCheck ($manifest.sourceCommit -ceq $receipt.sourceCommit -and $manifest.flavor -ceq 'self-contained') "Every installed payload hash matches the inspected self-contained ZIP during $Stage"
    Assert-InstallerCheck ((Read-UninstallValue 'DisplayVersion') -ceq $ExpectedVersion) "HKCU uninstall registration reports $ExpectedVersion during $Stage"
    Assert-InstallerCheck ([IO.Path]::GetFullPath((Read-UninstallValue 'InstallLocation')).TrimEnd([char]92) -ieq [IO.Path]::GetFullPath($installDirectory)) "Uninstall registration points to the chosen per-user directory during $Stage"
    Assert-InstallerCheck ((Read-UninstallValue 'Inno Setup: Icon Group') -ceq $groupName) "Installer registration records the fixed per-user Start menu group during $Stage"
    Assert-InstallerCheck (Test-Path -LiteralPath (Join-Path $shortcutDirectory 'Device Battery Status.lnk') -PathType Leaf) "Per-user application shortcut exists during $Stage"
    Assert-InstallerCheck (Test-Path -LiteralPath (Join-Path $shortcutDirectory 'Uninstall Device Battery Status.lnk') -PathType Leaf) "Per-user uninstall shortcut exists during $Stage"
}
function Invoke-SmokeUninstall([string]$Label) {
    $uninstallers = @(Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*.exe' -File)
    if ($uninstallers.Count -ne 1) { throw 'Expected exactly one registered Inno Setup uninstaller.' }
    Invoke-SmokeInstaller -Executable $uninstallers[0].FullName -Label $Label -Uninstall
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($null -eq (Read-UninstallValue 'DisplayName') -and
            @(Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*' -File -ErrorAction SilentlyContinue).Count -eq 0) { break }
        Start-Sleep -Milliseconds 100
    }
    Assert-InstallerCheck ($null -eq (Read-UninstallValue 'DisplayName')) "HKCU uninstall registration removed after $Label"
    foreach ($name in $script:ownedPayloadNames) {
        if (Test-Path -LiteralPath (Join-Path $installDirectory $name)) { throw "Owned installer payload remained after ${Label}: $name" }
    }
    Assert-InstallerCheck (@(Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*' -File -ErrorAction SilentlyContinue).Count -eq 0) "Uninstaller files removed after $Label"
    Assert-InstallerCheck (!(Test-Path -LiteralPath $shortcutDirectory)) "Owned per-user shortcuts removed after $Label"
    Assert-SettingsPreserved $Label
}

try {
    Assert-InstallerCheck ($receipt.signed -eq $false -and $receipt.applicationLaunched -eq $false -and $receipt.hardwareVerified -eq $false) 'Installer receipt preserves unsigned and no-application/no-hardware build claims'
    Assert-InstallerCheck ($receipt.sourceCommit -ceq $env:GITHUB_SHA) 'Installer smoke uses this exact workflow source'
    Assert-InstallerCheck ((Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $receipt.installer.sha256) 'Installer executable hash matches its build receipt'
    Assert-InstallerCheck ((Get-Content -LiteralPath ([IO.Path]::ChangeExtension($installer.FullName, '.sha256')) -Raw).Trim() -ceq "$($receipt.installer.sha256)  $($installer.Name)") 'Installer sibling checksum matches its bytes'
    if ((Test-Path -LiteralPath $installDirectory) -or (Test-Path -LiteralPath $dataDirectory) -or
        (Test-Path -LiteralPath $shortcutDirectory) -or
        $null -ne (Read-UninstallValue 'DisplayName') -or $null -ne (Read-StartupValue)) {
        throw 'Installer smoke requires a fresh isolated runner user profile; existing user data or registration was preserved.'
    }
    $machineBaseline = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($uninstallKey)
    try { if ($null -ne $machineBaseline) { throw 'Existing machine installation was preserved; installer smoke requires an isolated runner.' } }
    finally { if ($null -ne $machineBaseline) { $machineBaseline.Dispose() } }
    Assert-InstallerCheck (@(Get-Process -Name DeviceBatteryStatus -ErrorAction SilentlyContinue).Count -eq 0) 'No application process exists before installer smoke'
    New-Item -ItemType Directory -Force (Join-Path $dataDirectory 'logs') | Out-Null
    $dataCreated = $true
    [IO.File]::WriteAllText((Join-Path $dataDirectory 'settings.json'), '{"RefreshIntervalSeconds":77,"PreferredDeviceId":"smoke-device","CustomNames":{"smoke-device":"Preserved name"},"TrayIconMode":1,"TrayDeviceIds":["smoke-device"],"UpdateCheckMode":2,"FuturePreference":"preserve verbatim"}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $dataDirectory 'settings.json.bak'), '{"RefreshIntervalSeconds":60}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $dataDirectory 'capacity-health.json'), '{"fixture":"synthetic pre-existing capacity ledger; preserve verbatim"}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $dataDirectory 'capacity-health.json.bak'), '{"fixture":"synthetic capacity ledger backup"}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $dataDirectory 'logs/smoke-preservation.jsonl'), "{`"event`":`"preserve existing diagnostics`"}`n", [Text.UTF8Encoding]::new($false))
    $settingsHash = (Get-FileHash -LiteralPath (Join-Path $dataDirectory 'settings.json') -Algorithm SHA256).Hash
    $backupHash = (Get-FileHash -LiteralPath (Join-Path $dataDirectory 'settings.json.bak') -Algorithm SHA256).Hash
    $logHash = (Get-FileHash -LiteralPath (Join-Path $dataDirectory 'logs/smoke-preservation.jsonl') -Algorithm SHA256).Hash
    $healthHash = (Get-FileHash -LiteralPath (Join-Path $dataDirectory 'capacity-health.json') -Algorithm SHA256).Hash
    $healthBackupHash = (Get-FileHash -LiteralPath (Join-Path $dataDirectory 'capacity-health.json.bak') -Algorithm SHA256).Hash

    Invoke-SmokeInstaller $fixture.FullName 'install-prior-fixture'
    Assert-InstalledPayload '0.0.0-smoke-fixture' 'initial installation'
    Assert-InstallerCheck ($null -eq (Read-StartupValue)) 'Fresh installation does not create Windows startup registration'
    Assert-InstallerCheck (Test-Path -LiteralPath (Join-Path $installDirectory 'obsolete-upgrade-fixture.txt')) 'Controlled previous-installer fixture contains its obsolete owned file'
    Assert-SettingsPreserved 'initial install'
    $initialInstallPassed = $true
    [IO.File]::WriteAllText((Join-Path $installDirectory 'user-notes.smoke.txt'), 'Unowned user file must survive upgrade and uninstall.', [Text.UTF8Encoding]::new($false))
    Set-SmokeStartupValue $ownedCommand

    $created = $false
    $mutex = [Threading.Mutex]::new($true, 'Local\DeviceBatteryStatus', [ref]$created)
    try {
        if (!$created) { throw 'Another process unexpectedly owns the application mutex.' }
        Invoke-SmokeInstaller $installer.FullName 'blocked-running-application' $false
        Assert-InstallerCheck ((Read-UninstallValue 'DisplayVersion') -ceq '0.0.0-smoke-fixture') 'A held application mutex blocks upgrade before replacing installed files'
    }
    finally { if ($created) { $mutex.ReleaseMutex() }; $mutex.Dispose() }

    Invoke-SmokeInstaller $installer.FullName 'upgrade-to-current'
    Assert-InstalledPayload $receipt.version 'upgrade'
    Assert-InstallerCheck (!(Test-Path -LiteralPath (Join-Path $installDirectory 'obsolete-upgrade-fixture.txt'))) 'Upgrade removes obsolete files owned by the previous installer'
    Assert-InstallerCheck (Test-Path -LiteralPath (Join-Path $installDirectory 'user-notes.smoke.txt')) 'Upgrade preserves unowned files in the installation directory'
    Assert-InstallerCheck ((Read-StartupValue) -ceq $ownedCommand) 'Upgrade preserves an existing deliberate startup selection'
    Assert-SettingsPreserved 'upgrade'
    $upgradePassed = $true
    $ownedPayloadNames = @(Get-Content -LiteralPath (Join-Path $installDirectory '.installed-files.txt'))
    Invoke-SmokeUninstall 'uninstall-owned-startup'
    Assert-InstallerCheck ($null -eq (Read-StartupValue)) 'Uninstall removes only the exact startup command for this installed copy'
    Assert-InstallerCheck (Test-Path -LiteralPath (Join-Path $installDirectory 'user-notes.smoke.txt')) 'Uninstall preserves unowned user files'
    $ownedStartupUninstallPassed = $true

    Set-SmokeStartupValue $foreignCommand
    Invoke-SmokeInstaller $installer.FullName 'reinstall-current'
    Assert-InstalledPayload $receipt.version 'reinstallation'
    Assert-InstallerCheck ((Read-StartupValue) -ceq $foreignCommand) 'Install preserves startup registration belonging to another copy'
    $ownedPayloadNames = @(Get-Content -LiteralPath (Join-Path $installDirectory '.installed-files.txt'))
    Invoke-SmokeUninstall 'uninstall-foreign-startup'
    Assert-InstallerCheck ((Read-StartupValue) -ceq $foreignCommand) 'Uninstall preserves a foreign startup command byte for byte'
    $foreignStartupUninstallPassed = $true
    $machineKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($uninstallKey)
    try { Assert-InstallerCheck ($null -eq $machineKey) 'Per-user installer creates no machine-wide uninstall registration' }
    finally { if ($null -ne $machineKey) { $machineKey.Dispose() } }
    Assert-InstallerCheck (@(Get-Process -Name DeviceBatteryStatus -ErrorAction SilentlyContinue).Count -eq 0) 'Install, upgrade and uninstall never launch the application'
    $completed = $true
}
catch { $failure = $_.Exception.Message }
finally {
    # Clean only the isolated files/registry values created by this smoke run.
    if ($startupTouched -and (Read-StartupValue) -cin @($ownedCommand, $foreignCommand)) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($startupKey, $true)
        try { $key.DeleteValue($startupValue, $false) }
        finally { $key.Dispose() }
    }
    if ($completed) {
        if (Test-Path -LiteralPath $installDirectory) { Remove-Item -LiteralPath $installDirectory -Recurse -Force }
        if ($dataCreated) { Remove-Item -LiteralPath $dataDirectory -Recurse -Force }
    }
    $result = [ordered]@{
        schemaVersion = 1; sourceCommit = $receipt.sourceCommit; version = $receipt.version
        workflowRun = $env:GITHUB_RUN_ID; workflowAttempt = $env:GITHUB_RUN_ATTEMPT
        installerSha256 = $receipt.installer.sha256; installerBytes = $installer.Length
        passed = $completed; failure = $failure; checkCount = $checks.Count; checks = $checks.ToArray()
        initialInstallPassed = $initialInstallPassed; upgradePassed = $upgradePassed
        ownedStartupUninstallPassed = $ownedStartupUninstallPassed
        foreignStartupUninstallPassed = $foreignStartupUninstallPassed
        settingsPreserved = $completed; capacityHistoryPreserved = $completed; cleanUninstall = $completed
        installerProcesses = $processResults.ToArray(); installerExecuted = $installerExecuted
        applicationLaunched = $false; hardwareVerified = $false
        upgradeFixture = '0.0.0-smoke-fixture uses current application bytes; tests installer lifecycle only'
    }
    [IO.File]::WriteAllText((Join-Path $reportRoot 'installer-smoke.json'), ($result | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
}
if (!$completed) {
    $escaped = $failure.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Output "::error title=Per-user installer smoke::$escaped"
    exit 1
}
Write-Output "::notice title=Per-user installer smoke::Checks=$($checks.Count); Passed=true; SettingsPreserved=true; ApplicationLaunched=false; HardwareVerified=false"
