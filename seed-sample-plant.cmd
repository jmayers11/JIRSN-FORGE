@echo off
rem Builds a clearly labeled SAMPLE plant through the real Forge API, so the screens have something to show.
rem Start Forge first (run-forge.cmd) and leave it running. Safe to run once per database.
cd /d "%~dp0"
node tools\seed-sample-plant.mjs %*
echo.
pause
