#requires -Version 7.0
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Node
Push-Location (Join-Path $ProjectRoot 'frontend')
try {
    if (-not (Test-Path -LiteralPath 'node_modules')) { Invoke-Checked $NpmCommand @('ci', '--no-fund') }
    Invoke-Checked $NpmCommand @('run', 'dev')
} finally { Pop-Location }
