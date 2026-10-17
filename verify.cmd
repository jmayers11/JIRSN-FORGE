@echo off
rem Restores, builds and runs every test. Output goes to .forge-logs\verify.log;
rem the final line of .forge-logs\verify.status is PASS or FAIL.
setlocal
cd /d "%~dp0"
if not exist .forge-logs mkdir .forge-logs
rem Optional, not committed: forge.local.cmd can set FORGE_TEST_POSTGRES to use a local PostgreSQL instead of Docker.
if exist forge.local.cmd call forge.local.cmd
set "LOG=.forge-logs\verify.log"
echo RUNNING> .forge-logs\verify.status
echo === Forge verify %DATE% %TIME% ===> "%LOG%"
dotnet --version >> "%LOG%" 2>&1
if defined FORGE_TEST_POSTGRES (echo Database: local PostgreSQL from forge.local.cmd>> "%LOG%") else (echo Database: Docker>> "%LOG%")
echo === restore ===>> "%LOG%"
dotnet restore Forge.slnx >> "%LOG%" 2>&1 || goto fail
echo === build ===>> "%LOG%"
dotnet build Forge.slnx --no-restore -c Release -nologo -clp:NoSummary >> "%LOG%" 2>&1 || goto fail
echo === test ===>> "%LOG%"
rem V3.28: all integration test databases are restored from a pg_dump baseline.
set "FORGE_TEST_RESTORE_BASELINE=1"
echo PostgreSQL integration tests use restored migrated databases.>> "%LOG%"
rem V5: the full-system simulation runs separately from simulate.cmd.
echo Simulation scenarios (Category=Simulation) are excluded; run simulate.cmd.>> "%LOG%"
dotnet test Forge.slnx --no-build -c Release -nologo --filter "Category!=Simulation" --logger "console;verbosity=normal" >> "%LOG%" 2>&1 || goto fail
echo === UI hardening checks ===>> "%LOG%"
for %%f in (tests\ui\*.test.mjs) do node --test "%%f" >> "%LOG%" 2>&1 || goto fail
echo PASS> .forge-logs\verify.status
exit /b 0
:fail
echo FAIL> .forge-logs\verify.status
exit /b 1
