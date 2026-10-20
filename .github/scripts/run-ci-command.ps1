param([string]$Name, [string[]]$Command)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
function Write-CiAnnotation([string]$Kind, [string]$Title, [string]$Message) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Message)
    if ($bytes.Length -gt 16000) { $Message = [Text.Encoding]::UTF8.GetString($bytes, 0, 16000) + ' [truncated]' }
    $escaped = $Message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Output "::${Kind} title=${Title}::${escaped}"
}
New-Item -ItemType Directory -Force ci-logs | Out-Null
$log = "ci-logs/$Name.log"
if ($Name -eq 'focused' -and (Test-Path TestResults/focused.trx)) { Remove-Item TestResults/focused.trx -Force }
$program = $Command[0]
$arguments = $Command[1..($Command.Length - 1)]
& $program @arguments 2>&1 | Tee-Object -FilePath $log
$code = $LASTEXITCODE
Add-Content -Path $log -Value "`nnative_exit_code=$code"
if ($Name -eq 'focused') {
    try {
        [xml]$trx = Get-Content TestResults/focused.trx -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        foreach ($attribute in 'executed', 'passed', 'failed', 'notExecuted') {
            if (!$counts.HasAttribute($attribute)) { throw "Missing TRX counter: $attribute" }
        }
        Write-CiAnnotation notice 'Focused test results' "Executed=$($counts.executed); Passed=$($counts.passed); Failed=$($counts.failed); Skipped=$($counts.notExecuted); NativeExit=$code"
        foreach ($failure in $trx.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='Failed']")) {
            $errorInfo = $failure.SelectSingleNode(".//*[local-name()='ErrorInfo']")
            Write-CiAnnotation error 'Focused test failure' "$($failure.testName)`n$($errorInfo.Message)`n$($errorInfo.StackTrace)"
        }
        if ([int]$counts.executed -lt 1) { throw 'The focused run executed no tests.' }
        if ([int]$counts.failed -gt 0 -and $code -eq 0) { $code = 1 }
    } catch {
        Write-CiAnnotation error 'Focused test evidence unavailable' $_.Exception.Message
        if ($code -eq 0) { $code = 1 }
    }
}
if ($Name -eq 'offline') {
    $text = Get-Content $log -Raw
    $count = [regex]::Match($text, '(?m)^Ran (\d+) tests?\b')
    $skipped = [regex]::Match($text, '(?m)^(?:OK|FAILED).*\bskipped=(\d+)')
    $skipCount = if ($skipped.Success) { [int]$skipped.Groups[1].Value } else { 0 }
    if ($count.Success) {
        Write-CiAnnotation notice 'Offline parser test results' "Ran=$($count.Groups[1].Value); Skipped=$skipCount; NativeExit=$code"
    }
    if (!$count.Success -or [int]$count.Groups[1].Value -le $skipCount) {
        Write-CiAnnotation error 'Offline test evidence unavailable' 'The offline run executed no tests, or the runner did not report a count.'
        if ($code -eq 0) { $code = 1 }
    }
}
exit $code
