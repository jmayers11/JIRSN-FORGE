@echo off
rem V5 full-system simulation (Phase A). Builds and runs only tests\Forge.Simulation.Tests against disposable
rem PostgreSQL databases. Output goes to .forge-logs\simulate.log; scenario results go to .forge-logs\simulation;
rem the final line of .forge-logs\simulate.status is PASS or FAIL. It never touches the live forge database.
setlocal
cd /d "%~dp0"
if not exist .forge-logs mkdir .forge-logs
if exist forge.local.cmd call forge.local.cmd
set "LOG=.forge-logs\simulate.log"
echo RUNNING> .forge-logs\simulate.status
echo === Forge simulate %DATE% %TIME% ===> "%LOG%"
dotnet --version >> "%LOG%" 2>&1
if defined FORGE_TEST_POSTGRES (echo Database: local PostgreSQL from forge.local.cmd>> "%LOG%") else (echo Database: Docker>> "%LOG%")
set "PROJECT=tests\Forge.Simulation.Tests\Forge.Simulation.Tests.csproj"
echo === restore ===>> "%LOG%"
dotnet restore "%PROJECT%" >> "%LOG%" 2>&1 || goto fail
echo === build ===>> "%LOG%"
dotnet build "%PROJECT%" --no-restore -c Release -nologo -clp:NoSummary >> "%LOG%" 2>&1 || goto fail
echo === simulate ===>> "%LOG%"
set "FORGE_TEST_RESTORE_BASELINE=1"
dotnet test "%PROJECT%" --no-build -c Release -nologo --filter "Category=Simulation" --logger "console;verbosity=normal" >> "%LOG%" 2>&1 || goto fail
echo PASS> .forge-logs\simulate.status
exit /b 0
:fail
echo FAIL> .forge-logs\simulate.status
exit /b 1
