#requires -Version 7.0
. (Join-Path $PSScriptRoot 'Get-EvidenceSources.ps1')
$id = [guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) ('campus-evidence-isolation-' + $id)
try {
    foreach ($name in @('README.md','backend/Program.cs','frontend/src/types.ts','frontend/package.json','Data/DEMO-LOGIN.txt','.local/secret.json','backend/.env','backend/appsettings.Development.local.json','backend/bin/private.json','backend/private-uploads/document.json','frontend/node_modules/package.json','docs/evidence/old.json','scripts/output/stale.json')) {
        $path = Join-Path $root $name
        New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value 'synthetic fixture'
    }
    $actual = @(Get-EvidenceSources $root (Join-Path $root 'scripts/output') | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\','/') } | Sort-Object)
    $expected = @('README.md','backend/Program.cs','frontend/package.json','frontend/src/types.ts') | Sort-Object
    if (@(Compare-Object $expected $actual).Count -ne 0) { throw 'Evidence source enumeration included private/runtime files or omitted source.' }
    Write-Output 'PASS: explicit source selection excludes runtime credentials, uploads, dependencies and evidence outside Git.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') -or [IO.Path]::GetFileName($resolved) -ne ('campus-evidence-isolation-' + $id)) { throw 'Unsafe cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
