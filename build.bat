@echo off
setlocal
set "ROOT=%~dp0.."
cd /d "%ROOT%"

where dotnet >nul 2>nul
if errorlevel 1 (
    if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
        set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
    ) else (
        echo [ERROR] .NET 8 SDK not found. Install from https://dot.net
        exit /b 1
    )
) else (
    set "DOTNET=dotnet"
)

echo [1/3] Build...
"%DOTNET%" build ELC.sln -c Release || goto :fail

echo [2/3] Publish light (needs .NET 8 Desktop Runtime)...
"%DOTNET%" publish src\ElcBattery.App -c Release -r win-x64 --self-contained false -o publish\ELCBattery-light || goto :fail

echo [3/3] Publish standalone (single file, no dependency)...
"%DOTNET%" publish src\ElcBattery.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish\ELCBattery-standalone || goto :fail

echo.
echo Done:
echo   publish\ELCBattery-light\ELCBattery.exe      (small, needs runtime)
echo   publish\ELCBattery-standalone\ELCBattery.exe (single file, zero dependency)
exit /b 0

:fail
echo [ERROR] build failed
exit /b 1
