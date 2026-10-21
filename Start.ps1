$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
Write-Host 'SQL Job Runner: http://127.0.0.1:5295'
Write-Host 'Bu pencereyi kapatmak uygulamayi durdurur. Kesintisiz kullanim icin Windows servisi kurulumu README.md dosyasinda.'
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'SqlJobRunner.exe')) {
    & (Join-Path $PSScriptRoot 'SqlJobRunner.exe')
} else {
    dotnet run --project (Join-Path $PSScriptRoot 'SqlJobRunner.csproj') -c Release --no-launch-profile
}
exit $LASTEXITCODE
