@echo off
rem Starts a separate WhiskerWorks demo host at http://localhost:5090 on its own database "forge_whiskerworks_demo" (created if missing).
rem The PLANT1 preview (database "forge", port 5080) is not touched. Demo mode enables the simulated clock, baseline and reset,
rem and they still work only after the database is designated a demo-only tenant. Close this window to stop the demo host.
setlocal
cd /d "%~dp0"
if exist forge.local.cmd call forge.local.cmd
if not defined FORGE_TEST_POSTGRES (
  echo Set FORGE_TEST_POSTGRES in forge.local.cmd first. See forge.local.example.cmd.
  exit /b 1
)
set "ConnectionStrings__Forge=%FORGE_TEST_POSTGRES%;Database=forge_whiskerworks_demo"
set "ASPNETCORE_ENVIRONMENT=Development"
set "ASPNETCORE_URLS=http://localhost:5090"
set "Forge__Database__CreateIfMissing=true"
set "Forge__Demo__Enabled=true"
dotnet run --project src\Forge.Host -c Release --no-build --no-launch-profile
