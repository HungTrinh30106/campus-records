#requires -Version 7.0
param([switch]$Seed)
. (Join-Path $PSScriptRoot 'Common.ps1')
$oldSeed = $env:Seed__Enabled
try {
    $env:Seed__Enabled = if ($Seed) { 'true' } else { 'false' }
    Invoke-Checked dotnet @('run', '--project', $ApiProject, '--launch-profile', 'StudentRecords.Api')
} finally {
    if ($null -eq $oldSeed) { Remove-Item Env:Seed__Enabled -ErrorAction SilentlyContinue } else { $env:Seed__Enabled = $oldSeed }
}
