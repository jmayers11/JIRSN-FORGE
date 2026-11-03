@echo off
rem Builds dist\JISRN-Forge-Setup-VERSION.exe (owner decisions 2026-11-03): Forge published self-contained for Windows x64, plus the
rem PostgreSQL binaries from this PC (PGROOT, default PostgreSQL 18), embedded in one setup program. Set FORGE_VERSION to stamp it.
setlocal
cd /d "%~dp0.."
if not defined FORGE_VERSION set "FORGE_VERSION=1.0.0"
if not defined PGROOT set "PGROOT=C:\Program Files\PostgreSQL\18"
if not exist "%PGROOT%\bin\initdb.exe" (echo PostgreSQL not found in %PGROOT%. Set PGROOT. & exit /b 1)
set "STAGE=%CD%\.forge-logs\package"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%\payload"
echo === publish Forge ===
dotnet publish src\Forge.Host\Forge.Host.csproj -c Release -r win-x64 --self-contained true -o "%STAGE%\payload\app" -nologo -clp:NoSummary
if errorlevel 1 (echo PUBLISH FAILED & exit /b 1)
echo === PostgreSQL from %PGROOT% ===
for %%d in (bin lib share) do (robocopy "%PGROOT%\%%d" "%STAGE%\payload\pgsql\%%d" /E /NFL /NDL /NJH /NJS /NP >nul)
if not exist "%STAGE%\payload\pgsql\bin\postgres.exe" (echo POSTGRESQL COPY FAILED & exit /b 1)
> "%STAGE%\payload\pgsql\README-JISRN.txt" echo PostgreSQL is distributed under the PostgreSQL License (https://www.postgresql.org/about/licence/). Bundled unmodified for JISRN Forge.
> "%STAGE%\payload\version.txt" echo %FORGE_VERSION%
echo === payload ===
tar -a -c -f "%STAGE%\forge-payload.zip" -C "%STAGE%\payload" app pgsql version.txt
if errorlevel 1 (echo PAYLOAD FAILED & exit /b 1)
echo === setup program ===
dotnet publish src\Forge.Setup\Forge.Setup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ForgePayload="%STAGE%\forge-payload.zip" -p:ForgeVersion=%FORGE_VERSION% -o "%STAGE%\setup" -nologo -clp:NoSummary
if errorlevel 1 (echo SETUP BUILD FAILED & exit /b 1)
if not exist dist mkdir dist
copy /y "%STAGE%\setup\JISRN-Forge-Setup.exe" "dist\JISRN-Forge-Setup-%FORGE_VERSION%.exe" >nul
for %%f in ("dist\JISRN-Forge-Setup-%FORGE_VERSION%.exe") do echo PACKAGED %%~nxf %%~zf bytes
endlocal
