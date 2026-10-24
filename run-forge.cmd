@echo off
rem Starts Forge at http://localhost:5080 on the WhiskerWorks demo plant (WW-P01, fictional giant cat toys; database "forge_whiskerworks_demo").
rem Demo mode is on: the Scenarios page has the simulated clock, the saved baseline and Reset demo. The PLANT1 sample plant is kept
rem untouched in database "forge"; run-plant1.cmd starts it instead. Close this window to stop Forge.
rem Sign in with a demo identity, for example ww-demo-planner, ww-demo-supervisor, ww-demo-engineer, ww-operator-001 or dev-admin.
setlocal
cd /d "%~dp0"
if exist forge.local.cmd call forge.local.cmd
if not defined FORGE_TEST_POSTGRES (
  echo Set FORGE_TEST_POSTGRES in forge.local.cmd first. See forge.local.example.cmd.
  pause
  exit /b 1
)
set "ConnectionStrings__Forge=%FORGE_TEST_POSTGRES%;Database=forge_whiskerworks_demo"
set "ASPNETCORE_ENVIRONMENT=Development"
set "Forge__Database__CreateIfMissing=true"
set "Forge__Demo__Enabled=true"
start "" /min cmd /c "timeout /t 15 /nobreak >nul & start http://localhost:5080/"
dotnet run --project src\Forge.Host --launch-profile Forge.Host
