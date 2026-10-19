@echo off
rem V5 full-system simulation. Builds and runs only tests\Forge.Simulation.Tests against disposable
rem PostgreSQL databases. Output goes to .forge-logs\simulate.log; scenario results go to .forge-logs\simulation;
rem the final line of .forge-logs\simulate.status is PASS or FAIL. It never touches the live forge database.
rem Optional: set FORGE_SIM_FILTER (for example Phase=B) to run a subset; .forge-logs\simulate-batch.cmd does that.
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
rem Results from earlier runs are cleared so the summary reflects this run only.
if exist .forge-logs\simulation rmdir /s /q .forge-logs\simulation
set "FORGE_TEST_RESTORE_BASELINE=1"
set "FILTER=Category=Simulation&Mode!=Factory"
if defined FORGE_SIM_FILTER set "FILTER=Category=Simulation&Mode!=Factory&%FORGE_SIM_FILTER%"
echo Filter: "%FILTER%">> "%LOG%"
dotnet test "%PROJECT%" --no-build -c Release -nologo --filter "%FILTER%" --logger "console;verbosity=normal" >> "%LOG%" 2>&1 || goto fail
echo PASS> .forge-logs\simulate.status
exit /b 0
:fail
echo FAIL> .forge-logs\simulate.status
exit /b 1
