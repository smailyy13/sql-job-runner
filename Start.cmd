@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 exit /b 1

set "runnerExitCode=0"
if exist "SqlJobRunner.exe" goto runExe
if not exist "SqlJobRunner.csproj" (
    echo HATA: ZIP dosyasini tamamen bir klasore cikarin ve Start.cmd dosyasini oradan acin.
    goto failed
)

"%SystemRoot%\System32\where.exe" dotnet >nul 2>&1
if errorlevel 1 goto missingSdk
set "runnerSdkReady="
for /f "tokens=1 delims=." %%v in ('dotnet --list-sdks 2^>nul ^| findstr /r "^[0-9][0-9]*\."') do if %%v GEQ 8 set "runnerSdkReady=1"
if not defined runnerSdkReady goto missingSdk

echo Uygulama hazirlaniyor. Ilk acilista paketlerin indirilmesi biraz surebilir.
dotnet build "SqlJobRunner.csproj" -c Release --nologo
if errorlevel 1 goto failed
call :openBrowserWhenReady
echo SQL Job Runner: http://127.0.0.1:5295/
echo Bu pencereyi kapatmak uygulamayi durdurur.
dotnet run --project "SqlJobRunner.csproj" -c Release --no-launch-profile --no-build --no-restore
set "runnerExitCode=%errorlevel%"
goto finished

:runExe
call :openBrowserWhenReady
echo SQL Job Runner: http://127.0.0.1:5295/
echo Bu pencereyi kapatmak uygulamayi durdurur.
"%~dp0SqlJobRunner.exe"
set "runnerExitCode=%errorlevel%"
goto finished

:missingSdk
echo HATA: Kaynak kodu calistirmak icin .NET 8 SDK veya daha yeni bir SDK gereklidir.
echo Yalnizca .NET Runtime kurulmasi yeterli degildir.
echo Hazir EXE paketinde .NET kurulumu gerekmez.
goto failed

:openBrowserWhenReady
start "" /b powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -Command "$deadline = (Get-Date).AddSeconds(60); while ((Get-Date) -lt $deadline) { try { $response = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5295/' -TimeoutSec 2; if ($response.StatusCode -eq 200) { Start-Process 'http://127.0.0.1:5295/'; exit } } catch {}; Start-Sleep -Milliseconds 500 }"
exit /b 0

:failed
set "runnerExitCode=1"
:finished
if not "%runnerExitCode%"=="0" (
    echo.
    echo Uygulama baslatilamadi veya hata ile kapandi. Yukaridaki hata mesajini kontrol edin.
    pause
)
popd
exit /b %runnerExitCode%
