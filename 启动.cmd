@echo off
chcp 65001 >nul
cd /d "%~dp0"
dotnet run --project "%~dp0AlarmPushCenter\AlarmPushCenter.csproj" -- --config "%~dp0config.json"
if errorlevel 1 pause
