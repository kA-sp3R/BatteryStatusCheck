param(
    [Parameter(Mandatory)][ValidateSet('Debug', 'Release')][string]$Configuration,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedSource
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
function Write-TestAnnotation([string]$Kind, [string]$Title, [string]$Message) {
    $escaped = $Message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Output "::${Kind} title=${Title}::${escaped}"
}
$actualSource = git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualSource -ne $ExpectedSource) {
    throw 'Source commit does not match the requested SHA.'
}
$solution = Get-Content DeviceBatteryStatus.sln -Raw
$testProjects = @(Get-ChildItem tests -Recurse -Filter *.csproj | Where-Object {
    [xml]$project = Get-Content $_.FullName -Raw
    $project.Project.PropertyGroup.IsTestProject -contains 'true' -or
        @($project.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Microsoft.NET.Test.Sdk').Count -gt 0
})
if ($testProjects.Count -eq 0) { throw 'No test projects were found.' }
foreach ($project in $testProjects) {
    $relative = [IO.Path]::GetRelativePath((Get-Location).Path, $project.FullName).Replace('/', '\')
    if (!$solution.Contains($relative)) { throw "Test project is missing from the solution: $relative" }
}
New-Item -ItemType Directory -Force ci-logs, TestResults | Out-Null
Get-ChildItem TestResults -Filter *.trx | Remove-Item -Force
& dotnet test DeviceBatteryStatus.sln --configuration $Configuration --logger trx --results-directory TestResults 2>&1 |
    Tee-Object -FilePath "ci-logs/full-$Configuration.log"
$code = $LASTEXITCODE
Add-Content "ci-logs/full-$Configuration.log" "`nnative_exit_code=$code"
try {
    $results = @(Get-ChildItem TestResults -Filter *.trx)
    if ($results.Count -lt $testProjects.Count) { throw 'Not every test project produced a TRX report.' }
    $totals = @{ total = 0; executed = 0; passed = 0; failed = 0; notExecuted = 0 }
    foreach ($result in $results) {
        [xml]$trx = Get-Content $result.FullName -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        foreach ($key in @($totals.Keys)) {
            if (!$counts.HasAttribute($key)) { throw "Missing TRX counter: $key" }
            $totals[$key] += [int]$counts.GetAttribute($key)
        }
        foreach ($failure in $trx.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='Failed']")) {
            $errorInfo = $failure.SelectSingleNode(".//*[local-name()='ErrorInfo']")
            Write-TestAnnotation error 'Full software test failure' "$($failure.testName)`n$($errorInfo.Message)`n$($errorInfo.StackTrace)"
        }
    }
    if ($totals.executed -le 0 -or $totals.executed -ne $totals.total -or
        $totals.passed -ne $totals.total -or $totals.failed -ne 0 -or $totals.notExecuted -ne 0) {
        if ($code -eq 0) { $code = 1 }
    }
    $evidence = [ordered]@{
        sourceCommit = $actualSource
        configuration = $Configuration
        command = "dotnet test DeviceBatteryStatus.sln --configuration $Configuration --logger trx --results-directory TestResults"
        testProjects = @($testProjects | ForEach-Object { [IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName) })
        reportCount = $results.Count
        counters = $totals
        nativeExit = $code
        applicationLaunched = $false
        hardwareVerified = $false
    }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content "TestResults/full-$Configuration.json"
    Write-TestAnnotation notice 'Full software test results' "Source=$actualSource; Configuration=$Configuration; Projects=$($testProjects.Count); Executed=$($totals.executed); Passed=$($totals.passed); Failed=$($totals.failed); Skipped=$($totals.notExecuted); NativeExit=$code; ApplicationLaunched=false; HardwareVerified=false"
} catch {
    Write-TestAnnotation error 'Full software test evidence unavailable' $_.Exception.Message
    if ($code -eq 0) { $code = 1 }
}
exit $code
