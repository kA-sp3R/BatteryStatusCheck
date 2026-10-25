param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [string]$OutputDirectory = 'artifacts/smoke-harness'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$application = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$manifestPath = Join-Path $application 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$source = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $source -notmatch '^[0-9a-f]{40}$' -or $source -cne $env:GITHUB_SHA -or
    $manifest.sourceCommit -cne $source -or $manifest.runtimeIdentifier -cne 'win-x64' -or
    $manifest.flavor -notin 'framework-dependent', 'self-contained') {
    throw 'The inspected application package and checked-out harness source must match.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$applicationPrefix = $application.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($output.Equals($application, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($applicationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The separate harness must be staged outside the inspected application directory.'
}
New-Item -ItemType Directory -Force $output | Out-Null
$build = Join-Path $output 'build'
$launch = Join-Path $output 'launch'
foreach ($path in $build, $launch) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path | Out-Null
}

# File references compile the fixture against these exact package DLLs. No
# production project is rebuilt here and no harness files enter a normal ZIP.
& (Join-Path $PSScriptRoot 'run-ci-command.ps1') -Name 'build-separate-smoke-harness' -Command @(
    'dotnet', 'publish', (Join-Path $repositoryRoot 'tests/DeviceBatteryStatus.Smoke/DeviceBatteryStatus.Smoke.csproj'),
    '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'false', '--output', $build,
    "-p:SmokeApplicationDirectory=$application", "-p:SourceRevisionId=$source", '-p:DebugType=None', '-p:DebugSymbols=false')
if ($LASTEXITCODE -ne 0) { throw 'Separate synthetic harness compilation failed.' }

Get-ChildItem -LiteralPath $application -Force | Copy-Item -Destination $launch -Recurse -Force
foreach ($name in 'DeviceBatteryStatus.Smoke.exe', 'DeviceBatteryStatus.Smoke.dll') {
    Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $launch $name)
}
# Run with the actual package's framework requirements/runtime assets. This also
# lets the self-contained harness use the packaged hostfxr/coreclr, not global .NET.
Copy-Item -LiteralPath (Join-Path $application 'DeviceBatteryStatus.runtimeconfig.json') `
    -Destination (Join-Path $launch 'DeviceBatteryStatus.Smoke.runtimeconfig.json')
$deps = Get-Content -LiteralPath (Join-Path $application 'DeviceBatteryStatus.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$smokeKey = 'DeviceBatteryStatus.Smoke/1.0.0'
foreach ($target in $deps.targets.Values) {
    $target[$smokeKey] = [ordered]@{
        dependencies = [ordered]@{ DeviceBatteryStatus = $manifest.version }
        runtime = [ordered]@{ 'DeviceBatteryStatus.Smoke.dll' = [ordered]@{} }
    }
}
$deps.libraries[$smokeKey] = [ordered]@{ type = 'project'; serviceable = $false; sha512 = '' }
[IO.File]::WriteAllText((Join-Path $launch 'DeviceBatteryStatus.Smoke.deps.json'),
    ($deps | ConvertTo-Json -Depth 100) + "`n", [Text.UTF8Encoding]::new($false))
$files = [ordered]@{}
foreach ($name in 'DeviceBatteryStatus.Smoke.exe', 'DeviceBatteryStatus.Smoke.dll',
    'DeviceBatteryStatus.Smoke.runtimeconfig.json', 'DeviceBatteryStatus.Smoke.deps.json') {
    $files[$name] = (Get-FileHash -LiteralPath (Join-Path $launch $name) -Algorithm SHA256).Hash.ToLowerInvariant()
}
$evidence = [ordered]@{
    schemaVersion = 1; mode = 'separate-synthetic-wpf-harness'; sourceCommit = $source
    targetVersion = $manifest.version; targetFlavor = $manifest.flavor
    applicationManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    files = $files
}
[IO.File]::WriteAllText((Join-Path $launch 'smoke-harness-manifest.json'),
    ($evidence | ConvertTo-Json -Depth 10) + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "::notice title=Separate smoke harness staged::Source=$source; Flavor=$($manifest.flavor); ProductionPackageModified=false; HarnessDirectory=$launch"
