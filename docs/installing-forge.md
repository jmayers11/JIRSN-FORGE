# Installing JISRN Forge on a plant server

Owner decisions 2026-11-03: one plant server runs Forge as a Windows service with its own PostgreSQL; people use it in a browser.

## Build the installer

On a development PC with the .NET 10 SDK and PostgreSQL 18 installed:

```
tools\package-installer.cmd          rem dist\JISRN-Forge-Setup-1.0.0.exe  (set FORGE_VERSION to stamp another version)
tools\test-installer.cmd             rem builds it, then self-tests a full install in .forge-logs\selftest
```

The package is Forge published self-contained for Windows x64 plus the PostgreSQL binaries from `PGROOT`
(default `C:\Program Files\PostgreSQL\18`), embedded in one setup program (about 230 MB).
The self-test runs as the current user and changes nothing in Windows: it unpacks, creates and starts the database,
writes the settings and certificate, starts Forge, runs the first-run setup, signs in and loads the web UI.

## Install

Run `JISRN-Forge-Setup-<version>.exe` as an administrator on the server (Windows 10/11 or Server 2019+, x64).
Defaults: program in `C:\Program Files\JISRN Forge`, data in `C:\ProgramData\JISRN Forge`, web port 5443.

It installs and starts two services, both running as Network Service and restarting on failure:

| Service | What |
| --- | --- |
| `JISRNForgeDb` | PostgreSQL 18, port 5433, listening only on `localhost` |
| `JISRNForge` | Forge (HTTPS on the web port), starts after the database |

It also writes:

| File (data folder) | Holds |
| --- | --- |
| `forge.settings.json` | Database connection, HTTPS certificate, sign-in signing key. Readable only by SYSTEM, Administrators and the service. |
| `certs\forge.pfx`, `certs\forge.cer` | The self-signed certificate for the server's name (5 years). Give `forge.cer` to IT to trust on PCs. |
| `setup-code.txt` | The one-time code for the first-run setup. |
| `superuser.txt` | The PostgreSQL superuser password (maintenance only). Administrators only. |
| `pgdata\` | The database. Back up the whole data folder (or use `pg_dump`). |

Plus a Windows Firewall rule for the web port (domain and private networks, optional), a Start-menu shortcut,
and an Add/Remove Programs entry. `-quiet` installs with the defaults and no window (results in `setup.log` beside the program).

## First run

Open `https://<server>:5443/`. The browser warns once about the certificate until it is trusted.
Forge shows **Set up Forge**: enter the setup code, your name, a username and a password (10+ characters).
That person is the first security administrator; setup is closed once one exists. They then add people
(Access control → Register person, then **Set password** for a temporary password the person changes at first sign-in),
grant roles, and set up the plant (Plant Setup).

Sign-in (`Forge:Authentication:Mode = Local`): Forge's own accounts; five wrong passwords lock an account for 15 minutes;
actions that need a recent sign-in ask for the password again. To add a company identity provider (Nexus, Entra ID, ...),
set `Forge:Authentication:Authority`, `Audience` and `ClientId` in `forge.settings.json`; both sign-ins then work.

## Upgrade and remove

Running a newer setup on the server upgrades in place: it stops Forge, replaces the program, keeps the database,
settings, accounts and certificate, and Forge updates the database as it starts. Back up first.

Remove from Add/Remove Programs (or `JISRN-Forge-Setup.exe -uninstall`): it removes the services, firewall rule,
shortcut and program files, and asks whether to delete the data (No keeps it; `-uninstall -quiet` keeps it).

## Not yet

The WhiskerWorks demo cannot be loaded into an installed server yet (its seed scripts need the development tools).
