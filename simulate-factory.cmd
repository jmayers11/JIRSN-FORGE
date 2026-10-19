@echo off
rem V5 simulation Phase C: the full factory (baseline and disrupted runs on the same seed). Long-running: it runs only
rem the Mode=Factory test. Sizes default to the owner's (300 orders, 70 operators, 90 days); set FORGE_FACTORY_SIZE=smoke
rem for a small factory, or FORGE_FACTORY_ORDERS / _OPERATORS / _DAYS / _SEED. Output goes to .forge-logs\simulate-factory.log;
rem results to .forge-logs\simulation-factory; the final line of .forge-logs\simulate-factory.status is PASS or FAIL.
rem It never touches the live forge database. Stop the Forge server first: the build replaces its binaries.
setlocal
cd /d "%~dp0"
if not exist .forge-logs mkdir .forge-logs
if exist forge.local.cmd call forge.local.cmd
set "LOG=.forge-logs\simulate-factory.log"
echo RUNNING> .forge-logs\simulate-factory.status
echo === Forge factory simulation %DATE% %TIME% ===> "%LOG%"
dotnet --version >> "%LOG%" 2>&1
set "PROJECT=tests\Forge.Simulation.Tests\Forge.Simulation.Tests.csproj"
echo === restore ===>> "%LOG%"
dotnet restore "%PROJECT%" >> "%LOG%" 2>&1 || goto fail
echo === build ===>> "%LOG%"
dotnet build "%PROJECT%" --no-restore -c Release -nologo -clp:NoSummary >> "%LOG%" 2>&1 || goto fail
echo === simulate ===>> "%LOG%"
set "FORGE_SIM_OUT=%CD%\.forge-logs\simulation-factory"
if exist "%FORGE_SIM_OUT%" rmdir /s /q "%FORGE_SIM_OUT%"
set "FORGE_TEST_RESTORE_BASELINE=1"
echo Size: %FORGE_FACTORY_SIZE% orders=%FORGE_FACTORY_ORDERS% operators=%FORGE_FACTORY_OPERATORS% days=%FORGE_FACTORY_DAYS% seed=%FORGE_FACTORY_SEED%>> "%LOG%"
dotnet test "%PROJECT%" --no-build -c Release -nologo --filter "Category=Simulation&Mode=Factory" --logger "console;verbosity=normal" >> "%LOG%" 2>&1 || goto fail
echo PASS> .forge-logs\simulate-factory.status
exit /b 0
:fail
echo FAIL> .forge-logs\simulate-factory.status
exit /b 1
