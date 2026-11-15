#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ApiProject = Join-Path $ProjectRoot 'backend/StudentRecords.Api/StudentRecords.Api.csproj'
$NpmCommand = if ($IsWindows) { 'npm.cmd' } else { 'npm' }

function Invoke-Checked {
    param([string]$Command, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Lệnh $Command thất bại (mã $LASTEXITCODE)." }
}
function Assert-Node {
    $version = [version]((& node --version).Trim().TrimStart('v'))
    if ($version.Major -lt 22 -or ($version.Major -eq 22 -and $version.Minor -lt 12)) {
        throw 'Cần Node.js 22.12 trở lên.'
    }
}
