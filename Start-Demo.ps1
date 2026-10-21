$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Start-Process -FilePath (Join-Path $PSScriptRoot 'SqlJobRunner.exe') -ArgumentList '--DemoMode=true' -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
Start-Sleep -Seconds 2
Start-Process 'http://127.0.0.1:5296/'
