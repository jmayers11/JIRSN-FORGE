@echo off
rem Copy to forge.local.cmd (not committed) and put in your local PostgreSQL superuser password.
set "FORGE_TEST_POSTGRES=Host=localhost;Port=5432;Username=postgres;Password=CHANGE-ME;Database=postgres"
