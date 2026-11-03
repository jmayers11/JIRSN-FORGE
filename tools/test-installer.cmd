@echo off
rem Builds the installer, then runs its self-test: a full install in a scratch folder (database, settings, certificate, Forge,
rem first-run setup, sign-in) as the current user, without services, firewall rules or registry changes.
setlocal
cd /d "%~dp0.."
if not defined FORGE_VERSION set "FORGE_VERSION=1.0.0"
call tools\package-installer.cmd
if errorlevel 1 exit /b 1
set "T=%CD%\.forge-logs\selftest"
if exist "%T%" rmdir /s /q "%T%"
if exist dist\setup.log del dist\setup.log
set "__COMPAT_LAYER=RunAsInvoker"
echo === self-test ===
"dist\JISRN-Forge-Setup-%FORGE_VERSION%.exe" -selftest "%T%"
set "RESULT=%ERRORLEVEL%"
type dist\setup.log
if not "%RESULT%"=="0" (echo SELF-TEST FAILED & exit /b 1)
echo SELF-TEST OK
endlocal
