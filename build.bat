:: Builds Advanced PB Limiter for Torch, Magnetar and the Quasar plugin.
::
::   build.bat                    all of them, Release
::   build.bat torch              Torch only      -> Build\Advanced PB Limiter.zip
::   build.bat magnetar           Magnetar only   -> ServerPlugin\bin\<config>\net48 and net10.0
::   build.bat quasar             Quasar plugin and its companion only (needs a quasar checkout in ..\quasar)
::   build.bat all debug          all of them, Debug
::   build.bat magnetar release nodeploy    do not copy into %AppData%\Magnetar\...\Local
::
:: Torch needs the .NET Framework MSBuild (Visual Studio), Magnetar and Quasar need the .NET SDK (dotnet).
@echo off
setlocal
cd /d "%~dp0"

set TARGET=%~1
if "%TARGET%"=="" set TARGET=all
set CONFIG=%~2
if "%CONFIG%"=="" set CONFIG=Release
set DEPLOY=true
if /i "%~3"=="nodeploy" set DEPLOY=false

if /i "%TARGET%"=="all"      goto Valid
if /i "%TARGET%"=="torch"    goto Valid
if /i "%TARGET%"=="magnetar" goto Valid
if /i "%TARGET%"=="quasar"   goto Valid
echo Unknown target "%TARGET%". Use: torch, magnetar, quasar or all.
exit /b 2

:Valid
set FAILED=0

echo.
echo ===== Version =====
powershell -NoProfile -ExecutionPolicy Bypass -File Versioning\version.ps1
if errorlevel 1 (
    echo ERROR: Versioning\version.ps1 failed.
    exit /b 1
)

if /i "%TARGET%"=="magnetar" goto Magnetar
if /i "%TARGET%"=="quasar" goto Quasar

:Torch
echo.
echo ===== Torch (%CONFIG%) =====
if not exist GameBinaries\ (
    echo ERROR: GameBinaries junction missing. Run "Setup (run before opening solution).bat" first.
    set FAILED=1
    goto AfterTorch
)
set MSBUILD=
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do if not defined MSBUILD set MSBUILD=%%i
if not defined MSBUILD (
    echo ERROR: Visual Studio MSBuild not found ^(vswhere returned nothing^). Install Visual Studio with the .NET desktop workload.
    set FAILED=1
    goto AfterTorch
)
"%MSBUILD%" "TorchPlugin\Advanced PB Limiter.csproj" -p:Configuration=%CONFIG% -p:Platform=x64 -v:m -nologo
if errorlevel 1 (
    echo Torch build FAILED.
    set FAILED=1
) else (
    echo Torch build OK: %~dp0Build\Advanced PB Limiter.zip
)
:AfterTorch
if /i "%TARGET%"=="torch" goto Done

:Magnetar
echo.
echo ===== Magnetar (%CONFIG%) =====
if not exist Directory.Build.props (
    echo ERROR: Directory.Build.props missing. Run "Setup (run before opening solution).bat" first.
    set FAILED=1
    goto AfterMagnetar
)
dotnet build ServerPlugin\ServerPlugin.csproj -c %CONFIG% -p:DeployToMagnetar=%DEPLOY% -v:m -nologo
if errorlevel 1 (
    echo Magnetar build FAILED.
    set FAILED=1
) else (
    echo Magnetar build OK: %~dp0ServerPlugin\bin\%CONFIG%\
)
:AfterMagnetar
if /i "%TARGET%"=="magnetar" goto Done

:Quasar
echo.
echo ===== Quasar plugin (%CONFIG%) =====
:: Quasar builds the plugin itself when it is installed from QuasarHub. Local builds compile against a quasar checkout.
if not exist ..\quasar\Quasar.Plugin.Abstractions\ (
    echo Skipped: needs a quasar checkout next to this folder ^(..\quasar^).
    if /i "%TARGET%"=="quasar" set FAILED=1
    goto Done
)
dotnet build QuasarPlugin\AdvancedPBLimiter.Quasar\AdvancedPBLimiter.Quasar.csproj -c %CONFIG% -v:m -nologo
if errorlevel 1 (
    echo Quasar plugin build FAILED.
    set FAILED=1
    goto Done
)
dotnet build QuasarPlugin\AdvancedPBLimiter.QuasarCompanion\AdvancedPBLimiter.QuasarCompanion.csproj -c %CONFIG% -p:Platform=x64 -v:m -nologo
if errorlevel 1 (
    echo Quasar companion build FAILED.
    set FAILED=1
) else (
    echo Quasar plugin build OK: %~dp0QuasarPlugin
)
:Done
echo.
if "%FAILED%"=="1" (
    echo ===== BUILD FAILED =====
    exit /b 1
)
echo ===== BUILD OK =====
exit /b 0
