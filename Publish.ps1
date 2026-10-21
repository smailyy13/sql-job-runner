$ErrorActionPreference = 'Stop'
dotnet publish (Join-Path $PSScriptRoot 'SqlJobRunner.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $PSScriptRoot 'publish')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($file in @('Start.ps1','Start-Demo.ps1','Start-Demo.cmd','README.md','TEST-SENARYOLARI.md','sql/EdwJobGeneratorRequests.sql')) {
    New-Item -ItemType Directory -Force -Path (Split-Path (Join-Path $PSScriptRoot "publish/$file")) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $PSScriptRoot "publish/$file") -Force
}
Write-Host 'Hazir paket: publish klasoru. .NET kurulumu gerekmez.'
