#requires -Version 7.0
param([string]$Output = 'artifacts/evidence/native-db.json', [string]$MongodPath)
. (Join-Path $PSScriptRoot 'Common.ps1')
Push-Location $ProjectRoot
try {
    $arguments = @('run', '--project', 'tools/StudentRecords.SecurityLab', '-c', 'Release', '--', '--output', ([IO.Path]::GetFullPath($Output)))
    if ($MongodPath) { $arguments += @('--mongod', ([IO.Path]::GetFullPath($MongodPath))) }
    Invoke-Checked dotnet $arguments
} finally { Pop-Location }
