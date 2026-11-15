#requires -Version 7.0
param([string]$Database = 'campus_records')
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Node
if ($Database -notmatch '^[a-zA-Z0-9_-]{1,38}$') { throw 'Tên database chỉ dùng chữ, số, gạch ngang/gạch dưới, tối đa 38 ký tự để tương thích Atlas Free.' }
if (-not ((& dotnet --list-sdks) -match '^10\.')) { throw 'Cần cài .NET SDK 10.' }
Write-Host 'Cấu hình chỉ lưu trong .NET User Secrets của tài khoản Windows hiện tại, ngoài source.'
Write-Host 'User Secrets hỗ trợ development, không phải kho bí mật được mã hóa cho production.'
$secureUri = Read-Host 'MongoDB Atlas URI (hoặc mongodb://localhost:27017 để chạy local)' -AsSecureString
$securePassword = Read-Host 'Mật khẩu cho 3 tài khoản mẫu (10+ ký tự, hoa/thường/số/ký tự đặc biệt)' -AsSecureString
$confirmPassword = Read-Host 'Nhập lại mật khẩu mẫu' -AsSecureString
function Convert-Secret([Security.SecureString]$Secret) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}
$uri = Convert-Secret $secureUri
$password = Convert-Secret $securePassword
$confirmation = Convert-Secret $confirmPassword
try {
    if ($uri -notmatch '^mongodb(\+srv)?://') { throw 'URI phải bắt đầu bằng mongodb:// hoặc mongodb+srv://.' }
    if ($password -cne $confirmation) { throw 'Mật khẩu xác nhận không trùng khớp.' }
    if ($password.Length -lt 10 -or $password.Length -gt 128 -or $password -cnotmatch '[A-Z]' -or $password -cnotmatch '[a-z]' -or $password -notmatch '[0-9]' -or $password -notmatch '[^a-zA-Z0-9]' -or $password -match '\p{C}') {
        throw 'Mật khẩu chưa thỏa chính sách Identity.'
    }
    $uploadPath = Join-Path $ProjectRoot 'Data/uploads'
    New-Item -ItemType Directory -Force -Path $uploadPath | Out-Null
    $configuration = @{ 'Mongo:ConnectionString' = $uri; 'Mongo:Database' = $Database; 'Storage:Path' = $uploadPath; 'Seed:Enabled' = 'false'; 'Seed:Password' = $password }
    $configuration | ConvertTo-Json -Compress | & dotnet user-secrets set --project $ApiProject
    if ($LASTEXITCODE -ne 0) { throw 'Không lưu được user-secrets.' }
} finally {
    $uri = $null; $password = $null; $confirmation = $null; $configuration = $null
    $secureUri.Dispose(); $securePassword.Dispose(); $confirmPassword.Dispose()
}
Push-Location $ProjectRoot
try {
    Invoke-Checked dotnet @('restore', 'CampusRecords.slnx')
    Push-Location (Join-Path $ProjectRoot 'frontend')
    try { Invoke-Checked $NpmCommand @('ci', '--no-fund') } finally { Pop-Location }
} finally { Pop-Location }
Write-Host 'Đã cài thư viện. Mở hai terminal: scripts/Start-Api.ps1 -Seed và scripts/Start-Frontend.ps1.'
Write-Host 'Lần sau chạy Start-Api.ps1 không có -Seed. Mật khẩu seed chỉ dùng lần tạo dữ liệu đầu tiên.'
