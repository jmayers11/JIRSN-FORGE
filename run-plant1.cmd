@echo off
rem Starts Forge at http://localhost:5080 on the original PLANT1 sample plant (database "forge"). Use run-forge.cmd for the WhiskerWorks demo.
rem Development build: sign in with any registered Employee ID. Close this window to stop Forge.
setlocal
cd /d "%~dp0"
if exist forge.local.cmd call forge.local.cmd
if not defined ConnectionStrings__Forge if defined FORGE_TEST_POSTGRES set "ConnectionStrings__Forge=%FORGE_TEST_POSTGRES%;Database=forge"
if not defined ConnectionStrings__Forge (
  echo Set FORGE_TEST_POSTGRES or ConnectionStrings__Forge in forge.local.cmd first. See forge.local.example.cmd.
  pause
  exit /b 1
)
set "ASPNETCORE_ENVIRONMENT=Development"
set "Forge__Database__CreateIfMissing=true"
start "" /min cmd /c "timeout /t 15 /nobreak >nul & start http://localhost:5080/"
dotnet run --project src\Forge.Host --launch-profile Forge.Host
