#requires -Version 7.0
. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not $IsWindows) { throw 'This local setup helper targets Windows User Secrets. On other OS configure these keys using your secret store.' }
$secretFile = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft/UserSecrets/CampusRecords.LocalDevelopment/secrets.json'
if (-not (Test-Path -LiteralPath $secretFile)) { throw 'Run Setup.cmd first to configure MongoDB and the private upload directory.' }
$existing = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json -AsHashtable
$key = if ($existing.ContainsKey('Backup:Password') -and $existing['Backup:Password'].Length -ge 12) { $existing['Backup:Password'] } else { [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
$path = if ($existing.ContainsKey('Backup:Path')) { $existing['Backup:Path'] } else { Join-Path $ProjectRoot 'Data/backups' }
try {
    $values = @{ 'ChangeStreams:Enabled'='true'; 'Backup:Enabled'='true'; 'Backup:Path'=$path; 'Backup:Password'=$key; 'Backup:IntervalHours'='24'; 'Backup:RetentionCount'='7' }
    $values | ConvertTo-Json -Compress | & dotnet user-secrets set --project $ApiProject
    if ($LASTEXITCODE -ne 0) { throw 'Could not save operation settings.' }
    Write-Host 'Enabled change streams and daily encrypted backups (7 retained). Existing encryption key is preserved.'
    Write-Host 'Restart the API. Keep Backup:Password in a secure separate recovery store; never share secrets.json or commit it.'
} finally { $key=$null; $existing=$null; $values=$null }
