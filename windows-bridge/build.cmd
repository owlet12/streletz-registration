@rem Author: owlet (https://github.com/owlet12)
@rem Copyright (c) 2026 owlet
@echo off
setlocal
set "BRIDGE_ROOT=%~dp0.."
where dotnet >nul 2>nul
if errorlevel 1 (
    echo Rebuilding requires the .NET SDK. Install the .NET 8 SDK or newer, then run this script again.
    echo Running the ready StreletzBridge.exe requires only .NET Framework 4.8.
    exit /b 1
)
dotnet build "%~dp0build.csproj" -c Release
if errorlevel 1 exit /b 1
if not exist "%BRIDGE_ROOT%\dist" mkdir "%BRIDGE_ROOT%\dist"
copy /y "%~dp0bin\Release\net48\StreletzBridge.exe" "%BRIDGE_ROOT%\dist\StreletzBridge.exe" >nul
if errorlevel 1 exit /b 1
if exist "%~dp0bin\Release\net48\StreletzBridge.exe.config" (
    copy /y "%~dp0bin\Release\net48\StreletzBridge.exe.config" "%BRIDGE_ROOT%\dist\StreletzBridge.exe.config" >nul
    if errorlevel 1 exit /b 1
)
echo Created: %BRIDGE_ROOT%\dist\StreletzBridge.exe
echo The .NET SDK is needed only to rebuild. Running the EXE requires .NET Framework 4.8.
