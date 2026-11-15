#requires -Version 7.0
param([string]$OutputDirectory)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Get-EvidenceSources.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts/evidence/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$evidenceRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $evidenceRoot) -and @(Get-ChildItem -LiteralPath $evidenceRoot -Force).Count -gt 0) { throw 'Use a new, empty evidence directory to prevent stale test results.' }
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$startedAt = [DateTime]::UtcNow
$checks = [Collections.Generic.List[object]]::new()
function Invoke-EvidenceStep([string]$Name, [scriptblock]$Action) {
    $log = Join-Path $evidenceRoot ($Name + '.log')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action *>&1 | Tee-Object -FilePath $log | Out-Host
        $checks.Add([pscustomobject]@{ name=$Name; status='Passed'; durationMs=$watch.ElapsedMilliseconds; log=[IO.Path]::GetFileName($log) })
    } catch {
        $_.ToString() | Add-Content -LiteralPath $log
        $checks.Add([pscustomobject]@{ name=$Name; status='Failed'; durationMs=$watch.ElapsedMilliseconds; log=[IO.Path]::GetFileName($log) })
        Write-Warning "Evidence step failed: $Name. See its log."
    }
}
Push-Location $ProjectRoot
try {
    # Never load the application's Atlas URI here. Test.ps1 starts a disposable local server.
    if ($env:TEST_MONGO_URI -and $env:TEST_MONGO_URI -notmatch '^mongodb://(localhost|127\.0\.0\.1):[0-9]+/?$') { throw 'Evidence runner only accepts a local TEST_MONGO_URI. Clear that variable to create an isolated server.' }
    Invoke-EvidenceStep 'tests-and-build' { & (Join-Path $PSScriptRoot 'Test.ps1') -EvidenceDirectory $evidenceRoot }
    Invoke-EvidenceStep 'native-database-lab' { Invoke-Checked dotnet @('run', '--project', 'tools/StudentRecords.SecurityLab', '-c', 'Release', '--', '--output', (Join-Path $evidenceRoot 'native-db.json')) }
    Invoke-EvidenceStep 'xss-lab-isolation' { Invoke-Checked node @('--test', 'tools/xss-lab/server.test.mjs') }
    Invoke-EvidenceStep 'evidence-file-isolation' { & (Join-Path $PSScriptRoot 'Test-EvidenceIsolation.ps1') }
    Invoke-EvidenceStep 'nuget-audit' {
        $auditLines = @(& dotnet list CampusRecords.slnx package --vulnerable --include-transitive --no-restore --format json 2>&1)
        if ($LASTEXITCODE -ne 0) { throw 'NuGet audit command failed.' }
        $auditText = $auditLines -join [Environment]::NewLine
        $auditText | Set-Content -LiteralPath (Join-Path $evidenceRoot 'nuget-audit.json')
        if ($auditText -match 'NU1900|Unable to load|error:') { throw 'NuGet audit source could not be verified.' }
        $audit = $auditText | ConvertFrom-Json -AsHashtable
        foreach ($project in $audit.projects) {
            if ($project.ContainsKey('errors') -and $project.errors.Count -gt 0) { throw 'NuGet audit returned source errors.' }
            # NuGet omits frameworks entirely when this project has no advisories.
            if (-not $project.ContainsKey('frameworks')) { continue }
            foreach ($framework in $project.frameworks) {
                foreach ($key in @('topLevelPackages','transitivePackages')) {
                    if ($framework.ContainsKey($key)) { foreach ($package in $framework[$key]) { if ($package.ContainsKey('vulnerabilities') -and $package.vulnerabilities.Count -gt 0) { throw 'NuGet advisory found; review audit JSON.' } } }
                }
            }
        }
        Write-Output 'NuGet audit completed without reported vulnerable packages.'
    }
    Invoke-EvidenceStep 'npm-audit' {
        Push-Location (Join-Path $ProjectRoot 'frontend')
        try {
            $auditLines = @(& $NpmCommand audit --json 2>&1)
            $auditExit = $LASTEXITCODE
            $auditText = $auditLines -join [Environment]::NewLine
            $auditText | Set-Content -LiteralPath (Join-Path $evidenceRoot 'npm-audit.json')
            $audit = $auditText | ConvertFrom-Json -AsHashtable
            if ($auditExit -ne 0 -or $audit.metadata.vulnerabilities.total -ne 0) { throw 'npm audit returned a failure or advisory; see JSON.' }
            Write-Output 'npm audit: zero reported vulnerabilities.'
        } finally { Pop-Location }
    }
    Invoke-EvidenceStep 'source-secret-scan' {
        $files = @(Get-EvidenceSources $ProjectRoot $evidenceRoot)
        $findings = [Collections.Generic.List[string]]::new()
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($ProjectRoot, $file.FullName)
            $content = [IO.File]::ReadAllText($file.FullName)
            # Reports a filename only, never the matching secret text.
            if ($content -match 'mongodb(\+srv)?://[^/<>\s"'']+:[^@/<>\s"'']+@') { $findings.Add($relative) }
        }
        @{ checkedFiles=$files.Count; findings=@($findings); rule='Literal MongoDB URI containing username and password; ignored runtime data excluded' } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'source-scan.json')
        if ($findings.Count -gt 0) { throw ('Possible credentials found in: ' + ($findings -join ', ')) }
        Write-Output 'No literal credential-bearing MongoDB URI found in source.'
    }
    $passedDotnet = 0; $failedDotnet = 0; $skippedDotnet = 0
    foreach ($trxFile in @(Get-ChildItem -LiteralPath $evidenceRoot -Filter *.trx)) {
        [xml]$trx = Get-Content -LiteralPath $trxFile.FullName -Raw
        $passedDotnet += [int]$trx.TestRun.ResultSummary.Counters.passed
        $failedDotnet += [int]$trx.TestRun.ResultSummary.Counters.failed
        $skippedDotnet += [int]$trx.TestRun.ResultSummary.Counters.notExecuted
    }
    $frontendPassed = 0; $frontendFailed = 0; $nativePassed = 0; $nativeFailed = 0
    if (Test-Path -LiteralPath (Join-Path $evidenceRoot 'frontend-results.json')) { $frontend = Get-Content -LiteralPath (Join-Path $evidenceRoot 'frontend-results.json') -Raw | ConvertFrom-Json; $frontendPassed = $frontend.numPassedTests; $frontendFailed = $frontend.numFailedTests }
    if (Test-Path -LiteralPath (Join-Path $evidenceRoot 'native-db.json')) { $native = Get-Content -LiteralPath (Join-Path $evidenceRoot 'native-db.json') -Raw | ConvertFrom-Json; $nativePassed = @($native.cases | Where-Object status -eq 'Passed').Count; $nativeFailed = @($native.cases | Where-Object status -ne 'Passed').Count }
    $requiredResults = @('StudentRecords.Api.Tests.trx','StudentRecords.Backup.Tests.trx','frontend-results.json','native-db.json')
    $complete = @($requiredResults | Where-Object { -not (Test-Path -LiteralPath (Join-Path $evidenceRoot $_)) }).Count -eq 0
    $allResultsPassed = $complete -and $failedDotnet -eq 0 -and $skippedDotnet -eq 0 -and $frontendFailed -eq 0 -and $frontend.numPendingTests -eq 0 -and $nativeFailed -eq 0 -and $native.success -eq $true
    $manifest = foreach ($file in @(Get-EvidenceSources $ProjectRoot $evidenceRoot)) { [pscustomobject]@{ path=[IO.Path]::GetRelativePath($ProjectRoot, $file.FullName); sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash } }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'source-manifest.json')
    $summary = [ordered]@{ startedAt=$startedAt; completedAt=[DateTime]::UtcNow; timezone='Asia/Ho_Chi_Minh'; environment='Disposable local MongoDB; Atlas application data is not used'; checks=@($checks); dotnetPassed=$passedDotnet; dotnetFailed=$failedDotnet; frontendPassed=$frontendPassed; frontendFailed=$frontendFailed; nativePassed=$nativePassed; nativeFailed=$nativeFailed; success=(@($checks | Where-Object status -eq 'Failed').Count -eq 0 -and $passedDotnet -gt 0 -and $frontendPassed -gt 0 -and $nativePassed -eq 12) }
    $summary.success = $summary.success -and $allResultsPassed
    $summary.dotnetSkipped = $skippedDotnet
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'summary.json')
    Write-Output "Evidence saved: $evidenceRoot"
    if (-not $summary.success) { throw 'Evidence contains failures or missing results; inspect summary.json.' }
} finally { Pop-Location }
