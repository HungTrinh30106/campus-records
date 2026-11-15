#requires -Version 7.0
param([ValidateRange(1024,65535)][int]$Port = 5180)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Node
Invoke-Checked node @((Join-Path $ProjectRoot 'tools/xss-lab/server.mjs'), '--port', "$Port")
