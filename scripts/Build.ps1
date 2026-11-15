#requires -Version 7.0
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Node
Push-Location $ProjectRoot
try {
    Push-Location (Join-Path $ProjectRoot 'frontend')
    try {
        if (-not (Test-Path -LiteralPath 'node_modules')) { Invoke-Checked $NpmCommand @('ci', '--no-fund') }
        Invoke-Checked $NpmCommand @('run', 'build')
    } finally { Pop-Location }
    $output = Join-Path $ProjectRoot ('artifacts/publish-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    Invoke-Checked dotnet @('publish', $ApiProject, '-c', 'Release', '-o', $output)
    New-Item -ItemType Directory -Path (Join-Path $output 'wwwroot') -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'frontend/dist') -Force | Copy-Item -Destination (Join-Path $output 'wwwroot') -Recurse
    Write-Host "Bản publish gồm C# và frontend: $output"
    Write-Host 'Xem docs/DEPLOYMENT.md để cấu hình HTTPS, secret và thư mục dữ liệu trước khi chạy production.'
} finally { Pop-Location }
