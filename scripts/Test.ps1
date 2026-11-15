#requires -Version 7.0
param([string]$MongoUri, [string]$EvidenceDirectory)
. (Join-Path $PSScriptRoot 'Common.ps1')
$mongoProcess = $null
$tempRoot = $null
$originalUri = $env:TEST_MONGO_URI
try {
    if ($MongoUri) { $env:TEST_MONGO_URI = $MongoUri }
    elseif (-not $env:TEST_MONGO_URI) {
        $binary = Get-Command mongod -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
        if (-not $binary -and $IsWindows) {
            $serverRoot = Join-Path $env:ProgramFiles 'MongoDB/Server'
            if (Test-Path -LiteralPath $serverRoot) {
                $binary = Get-ChildItem -LiteralPath $serverRoot -Directory | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'bin/mongod.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            }
        }
        if (-not $binary) { throw 'Cài MongoDB Community để chạy tests, hoặc truyền -MongoUri đến một máy MongoDB thử nghiệm. Không dùng URI production.' }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
        $tempRoot = Join-Path $ProjectRoot ('.local/test-mongo-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
        $arguments = @('--dbpath', ('"' + $tempRoot + '"'), '--port', "$port", '--bind_ip', '127.0.0.1', '--logpath', ('"' + (Join-Path $tempRoot 'mongo.log') + '"'), '--quiet')
        if ($IsWindows) { $mongoProcess = Start-Process -FilePath $binary -ArgumentList $arguments -WindowStyle Hidden -PassThru }
        else { $mongoProcess = Start-Process -FilePath $binary -ArgumentList $arguments -PassThru }
        $ready = $false
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($mongoProcess.HasExited) { throw 'Mongo test child exited before readiness; no test URI will be used.' }
            $logPath = Join-Path $tempRoot 'mongo.log'
            if (Test-Path -LiteralPath $logPath) {
                $logStream = [IO.File]::Open($logPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                $logReader = [IO.StreamReader]::new($logStream)
                try { $logLines = $logReader.ReadToEnd().Split("`n") } finally { $logReader.Dispose() }
                foreach ($line in $logLines) {
                    if ([string]::IsNullOrWhiteSpace($line)) { continue }
                    try {
                        $entry = $line | ConvertFrom-Json -AsHashtable
                        if ($entry.ContainsKey('msg') -and $entry.msg -eq 'Waiting for connections' -and $entry.attr.port -eq $port) { $ready = $true }
                    } catch [ArgumentException] { }
                }
            }
            if ($ready) { break }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready -or $mongoProcess.HasExited) { throw 'Cannot prove readiness of the private Mongo test child.' }
        $env:TEST_MONGO_URI = "mongodb://127.0.0.1:$port"
    }
    Push-Location $ProjectRoot
    try {
        if ($EvidenceDirectory) {
            $evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
            New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
            foreach ($testProject in @(Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'tests') -Recurse -Filter *.csproj -File | Select-Object -ExpandProperty FullName)) {
                $name = [IO.Path]::GetFileNameWithoutExtension($testProject)
                Invoke-Checked dotnet @('test', $testProject, '-c', 'Release', '--verbosity', 'minimal', '--logger', "trx;LogFileName=$name.trx", '--results-directory', $evidenceRoot)
            }
        } else { Invoke-Checked dotnet @('test', 'CampusRecords.slnx', '-c', 'Release', '--verbosity', 'minimal') }
        Push-Location (Join-Path $ProjectRoot 'frontend')
        try {
            if ($EvidenceDirectory) { Invoke-Checked $NpmCommand @('test', '--', '--reporter=default', '--reporter=json', "--outputFile=$(Join-Path $evidenceRoot 'frontend-results.json')") }
            else { Invoke-Checked $NpmCommand @('test') }
            Invoke-Checked $NpmCommand @('run', 'build')
        } finally { Pop-Location }
    } finally { Pop-Location }
} finally {
    if ($mongoProcess -and -not $mongoProcess.HasExited) { Stop-Process -InputObject $mongoProcess; $mongoProcess.WaitForExit() }
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        $resolved = [IO.Path]::GetFullPath($tempRoot)
        if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath((Join-Path $ProjectRoot '.local')) -or [IO.Path]::GetFileName($resolved) -notmatch '^test-mongo-[a-f0-9]{32}$') { throw 'Unsafe test cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    if ($null -eq $originalUri) { Remove-Item Env:TEST_MONGO_URI -ErrorAction SilentlyContinue } else { $env:TEST_MONGO_URI = $originalUri }
}
