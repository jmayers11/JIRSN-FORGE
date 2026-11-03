using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Npgsql;

namespace Forge.Setup;

/// <summary>What a new installation is set up with. Upgrades keep what was chosen before.</summary>
public sealed record InstallOptions(string InstallDirectory, string DataDirectory, int WebPort, int DatabasePort, bool OpenFirewall)
{
    public static InstallOptions Defaults => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JISRN Forge"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JISRN Forge"),
        5443, 5433, true);
}

/// <summary>A finished installation: where people open Forge and the one-time setup code for the first administrator.</summary>
public sealed record InstallResult(string Url, string? SetupCode, string CertificatePath, bool Upgraded);

/// <summary>What is already installed on this computer, from the registry.</summary>
public sealed record ExistingInstall(string InstallDirectory, string DataDirectory, string Version, int WebPort);

/// <summary>
/// Installs, upgrades, removes and self-tests JISRN Forge on one Windows server (owner decisions 2026-11-03): Forge runs as a Windows
/// service (Network Service) over HTTPS with a self-signed certificate; it brings its own PostgreSQL, also a service, listening only on
/// this computer, with random passwords; the first security administrator is created in the browser with a one-time setup code.
/// An upgrade replaces the program and keeps the database, settings and certificate.
/// </summary>
public sealed class SetupEngine(IProgress<string> progress)
{
    public const string ForgeService = "JISRNForge";
    public const string DatabaseService = "JISRNForgeDb";
    private const string RegistryKey = @"SOFTWARE\JISRN\Forge";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\JISRNForge";
    private const string NetworkService = "*S-1-5-20";
    private const string AppRole = "forge_app";
    private const string Superuser = "postgres";

    public static string Version => typeof(SetupEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static ExistingInstall? FindExisting()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
        if (key?.GetValue("InstallDirectory") is not string install || key.GetValue("DataDirectory") is not string data) return null;
        return new ExistingInstall(install, data, key.GetValue("Version") as string ?? "?", key.GetValue("WebPort") is int port ? port : 5443);
    }

    // ------------------------------------------------------------------------------------------------ install and upgrade

    public async Task<InstallResult> InstallAsync(InstallOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var existing = FindExisting();
        if (existing is not null) return await UpgradeAsync(existing, ct);

        var app = Path.Combine(options.InstallDirectory, "app");
        var pgsql = Path.Combine(options.InstallDirectory, "pgsql");
        var data = options.DataDirectory;
        var pgdata = Path.Combine(data, "pgdata");
        if (Directory.Exists(pgdata) && Directory.EnumerateFileSystemEntries(pgdata).Any())
            throw new InvalidOperationException($"{pgdata} already holds a database. Move it away or choose another data folder.");
        PortFree(options.WebPort, "web");
        PortFree(options.DatabasePort, "database");

        Say("Copying the program files…");
        Extract(options.InstallDirectory);
        foreach (var folder in new[] { "pgdata", "certs", "keys", "pki", "interchange", "logs" }) Directory.CreateDirectory(Path.Combine(data, folder));

        Say("Creating the database…");
        var superPassword = Secret(24);
        var appPassword = Secret(24);
        await InitDatabaseAsync(pgsql, pgdata, superPassword, options.DatabasePort, ct);
        Run("icacls", $"\"{data}\" /grant \"{NetworkService}:(OI)(CI)M\" /T /Q");
        Run("icacls", $"\"{pgdata}\" /grant \"{NetworkService}:(OI)(CI)F\" /T /Q");
        Run(Path.Combine(pgsql, "bin", "pg_ctl.exe"), $"register -N {DatabaseService} -U \"NT AUTHORITY\\NetworkService\" -D \"{pgdata}\" -S auto -w");
        Run("sc.exe", $"description {DatabaseService} \"Database for JISRN Forge. Listens only on this computer.\"");
        Run("sc.exe", $"failure {DatabaseService} reset= 86400 actions= restart/5000/restart/30000/restart/60000");
        StartService(DatabaseService);
        await CreateForgeDatabaseAsync(options.DatabasePort, superPassword, appPassword, ct);

        Say("Making the HTTPS certificate…");
        var (pfx, cer, certPassword) = MakeCertificate(Path.Combine(data, "certs"));
        var setupCode = SetupCode();
        var codeFile = Path.Combine(data, "setup-code.txt");
        File.WriteAllText(codeFile, setupCode + Environment.NewLine);
        var settings = Path.Combine(data, "forge.settings.json");
        File.WriteAllText(settings, Settings(options, data, appPassword, pfx, certPassword, codeFile));
        // Only the system, administrators and the service may read the settings (they hold the database password and signing key).
        foreach (var secret in new[] { settings, pfx, codeFile })
            Run("icacls", $"\"{secret}\" /inheritance:r /grant:r \"*S-1-5-18:F\" \"*S-1-5-32-544:F\" \"{NetworkService}:R\" /Q");
        File.WriteAllText(Path.Combine(data, "superuser.txt"),
            $"PostgreSQL superuser for JISRN Forge (keep safe; needed only for maintenance).{Environment.NewLine}Port {options.DatabasePort}, user {Superuser}, password {superPassword}{Environment.NewLine}");
        Run("icacls", $"\"{Path.Combine(data, "superuser.txt")}\" /inheritance:r /grant:r \"*S-1-5-18:F\" \"*S-1-5-32-544:F\" /Q");

        Say("Registering the Forge service…");
        var exe = Path.Combine(app, "Forge.Host.exe");
        Run("sc.exe", $"create {ForgeService} binPath= \"\\\"{exe}\\\"\" start= delayed-auto obj= \"NT AUTHORITY\\NetworkService\" DisplayName= \"JISRN Forge\" depend= {DatabaseService}");
        Run("sc.exe", $"description {ForgeService} \"JISRN Forge manufacturing operations (https://{Environment.MachineName}:{options.WebPort}/).\"");
        Run("sc.exe", $"failure {ForgeService} reset= 86400 actions= restart/5000/restart/30000/restart/60000");
        if (options.OpenFirewall)
        {
            Say("Allowing other computers through Windows Firewall…");
            Run("netsh", $"advfirewall firewall add rule name=\"JISRN Forge (HTTPS {options.WebPort})\" dir=in action=allow protocol=TCP localport={options.WebPort} profile=domain,private");
        }

        Register(options, cer);
        Say("Starting Forge and preparing the database (the first start takes a minute or two)…");
        StartService(ForgeService);
        var thumbprint = X509CertificateLoader.LoadCertificateFromFile(cer).Thumbprint;
        await WaitReadyAsync(options.WebPort, thumbprint, TimeSpan.FromMinutes(5), ct);
        Shortcut(options.WebPort);
        var url = $"https://{Environment.MachineName.ToLowerInvariant()}:{options.WebPort}/";
        Say("Forge is running at " + url);
        return new InstallResult(url, setupCode, cer, false);
    }

    private async Task<InstallResult> UpgradeAsync(ExistingInstall existing, CancellationToken ct)
    {
        Say($"Upgrading Forge {existing.Version} to {Version}. The database, settings and certificate are kept.");
        StopService(ForgeService);
        var app = Path.Combine(existing.InstallDirectory, "app");
        if (Directory.Exists(app)) Directory.Delete(app, true);
        Say("Copying the program files…");
        Extract(existing.InstallDirectory, onlyApp: true);
        using (var key = Registry.LocalMachine.CreateSubKey(RegistryKey)) key.SetValue("Version", Version);
        using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey)) key.SetValue("DisplayVersion", Version);
        CopySelf(existing.InstallDirectory);
        Say("Starting Forge (it updates the database as it starts)…");
        StartService(DatabaseService);
        StartService(ForgeService);
        var cer = Path.Combine(existing.DataDirectory, "certs", "forge.cer");
        await WaitReadyAsync(existing.WebPort, X509CertificateLoader.LoadCertificateFromFile(cer).Thumbprint, TimeSpan.FromMinutes(10), ct);
        return new InstallResult($"https://{Environment.MachineName.ToLowerInvariant()}:{existing.WebPort}/", null, cer, true);
    }

    // ------------------------------------------------------------------------------------------------ uninstall

    public void Uninstall(bool deleteData)
    {
        var existing = FindExisting() ?? throw new InvalidOperationException("JISRN Forge is not installed on this computer.");
        Say("Stopping and removing the services…");
        StopService(ForgeService);
        TryRun("sc.exe", $"delete {ForgeService}");
        StopService(DatabaseService);
        TryRun("sc.exe", $"delete {DatabaseService}");
        TryRun("netsh", $"advfirewall firewall delete rule name=\"JISRN Forge (HTTPS {existing.WebPort})\"");
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "JISRN Forge.url");
        if (File.Exists(shortcut)) File.Delete(shortcut);
        Say("Removing the program files…");
        foreach (var folder in new[] { "app", "pgsql" })
        {
            var path = Path.Combine(existing.InstallDirectory, folder);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        if (deleteData && Directory.Exists(existing.DataDirectory))
        {
            Say("Deleting the database and settings…");
            Directory.Delete(existing.DataDirectory, true);
        }

        Registry.LocalMachine.DeleteSubKeyTree(RegistryKey, false);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        // The running setup program cannot delete itself; Windows removes it at the next restart.
        var self = Path.Combine(existing.InstallDirectory, "JISRN-Forge-Setup.exe");
        if (File.Exists(self)) MoveFileEx(self, null, 4);
        Say(deleteData ? "JISRN Forge and its data are removed." : $"JISRN Forge is removed. Its data is kept in {existing.DataDirectory}.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string? replacement, int flags);

    // ------------------------------------------------------------------------------------------------ self-test

    /// <summary>
    /// Everything an install does that needs no administrator, in a scratch folder: unpack, create and start the database, write the
    /// settings and certificate, start Forge as a plain process, then run the first-run setup, sign in and read who is signed in.
    /// No service, firewall rule or registry entry is made.
    /// </summary>
    public async Task<bool> SelfTestAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder);
        var data = Path.Combine(root, "data");
        var pgdata = Path.Combine(data, "pgdata");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException($"{root} must be empty.");
        var webPort = FreePort();
        var dbPort = FreePort();
        var options = new InstallOptions(root, data, webPort, dbPort, false);
        Process? host = null;
        var pgctl = Path.Combine(root, "pgsql", "bin", "pg_ctl.exe");
        try
        {
            Say("Unpacking…");
            Extract(root);
            foreach (var f in new[] { "pgdata", "certs", "keys", "pki", "interchange", "logs" }) Directory.CreateDirectory(Path.Combine(data, f));
            var superPassword = Secret(24);
            var appPassword = Secret(24);
            await InitDatabaseAsync(Path.Combine(root, "pgsql"), pgdata, superPassword, dbPort, ct);
            // pg_ctl start leaves PostgreSQL running with pg_ctl's output handles, so its output is not captured (it would never end).
            Run(pgctl, $"start -D \"{pgdata}\" -l \"{Path.Combine(data, "logs", "postgres.log")}\" -w -t 60", capture: false);
            await CreateForgeDatabaseAsync(dbPort, superPassword, appPassword, ct);
            var (pfx, cer, certPassword) = MakeCertificate(Path.Combine(data, "certs"));
            var code = SetupCode();
            var codeFile = Path.Combine(data, "setup-code.txt");
            await File.WriteAllTextAsync(codeFile, code, ct);
            var settings = Path.Combine(data, "forge.settings.json");
            await File.WriteAllTextAsync(settings, Settings(options, data, appPassword, pfx, certPassword, codeFile), ct);

            Say("Starting Forge…");
            var start = new ProcessStartInfo(Path.Combine(root, "app", "Forge.Host.exe")) { WorkingDirectory = Path.Combine(root, "app"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["FORGE_SETTINGS"] = settings;
            start.Environment.Remove("ASPNETCORE_ENVIRONMENT");
            start.Environment.Remove("ASPNETCORE_URLS");
            start.Environment.Remove("ConnectionStrings__Forge");
            host = Process.Start(start)!;
            var log = new StringBuilder();
            host.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
            host.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
            host.BeginOutputReadLine();
            host.BeginErrorReadLine();
            var thumbprint = X509CertificateLoader.LoadCertificateFromFile(cer).Thumbprint;
            try
            {
                await WaitReadyAsync(webPort, thumbprint, TimeSpan.FromMinutes(5), ct, host);
            }
            catch
            {
                lock (log) Say("Forge output:" + Environment.NewLine + log);
                throw;
            }

            using var http = Client(thumbprint, webPort);
            var status = await http.GetFromJsonAsync<JsonElement>("api/setup/status", ct);
            Check(status.GetProperty("needed").GetBoolean(), "setup is offered on a new server");
            Check((await http.PostAsJsonAsync("api/setup/", new { setupCode = "WRONG-CODE-0", displayName = "Self Test", username = "selftest", password = "self-test-password" }, ct)).StatusCode == HttpStatusCode.Forbidden,
                "a wrong setup code is refused");
            var created = await http.PostAsJsonAsync("api/setup/", new { setupCode = code, displayName = "Self Test", username = "selftest", password = "self-test-password" }, ct);
            Check(created.IsSuccessStatusCode, "the first administrator is created: " + await created.Content.ReadAsStringAsync(ct));
            var signIn = await http.PostAsJsonAsync("api/auth/local/sign-in", new { username = "selftest", password = "self-test-password" }, ct);
            Check(signIn.IsSuccessStatusCode, "the administrator signs in");
            var token = (await signIn.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("token").GetString();
            using var me = new HttpRequestMessage(HttpMethod.Get, "api/kernel/me");
            me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var who = await http.SendAsync(me, ct);
            Check(who.IsSuccessStatusCode && (await who.Content.ReadAsStringAsync(ct)).Contains("Self Test", StringComparison.Ordinal), "the signed-in administrator is recognized");
            Check((await http.GetAsync(new Uri("/", UriKind.Relative), ct)).IsSuccessStatusCode, "the web UI is served");
            Say("SELF-TEST PASSED");
            return true;
        }
        finally
        {
            if (host is { HasExited: false })
            {
                host.Kill(true);
                await host.WaitForExitAsync(CancellationToken.None);
            }

            if (File.Exists(Path.Combine(pgdata, "postmaster.pid"))) TryRun(pgctl, $"stop -D \"{pgdata}\" -m fast -w");
        }
    }

    private void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException("Self-test failed: " + what);
        Say("  ok: " + what);
    }

    // ------------------------------------------------------------------------------------------------ steps

    private void Extract(string target, bool onlyApp = false)
    {
        using var stream = Payload();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            if (onlyApp && !entry.FullName.StartsWith("app/", StringComparison.Ordinal)) continue;
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installer payload is damaged.");
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
            if (++count % 500 == 0) Say(string.Create(CultureInfo.InvariantCulture, $"  {count} files…"));
        }

        CopySelf(target);
    }

    private static void CopySelf(string target)
    {
        var self = Environment.ProcessPath;
        var copy = Path.Combine(target, "JISRN-Forge-Setup.exe");
        if (self is not null && !string.Equals(Path.GetFullPath(self), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, copy, true);
    }

    private static Stream Payload()
    {
        var embedded = typeof(SetupEngine).Assembly.GetManifestResourceStream("forge-payload.zip");
        if (embedded is not null) return embedded;
        var beside = Path.Combine(AppContext.BaseDirectory, "forge-payload.zip");
        return File.Exists(beside) ? File.OpenRead(beside) : throw new FileNotFoundException("This setup program has no Forge payload; rebuild it with tools\\package-installer.cmd.");
    }

    private async Task InitDatabaseAsync(string pgsql, string pgdata, string superPassword, int port, CancellationToken ct)
    {
        var pwfile = Path.Combine(Path.GetTempPath(), "forge-pw-" + Secret(8) + ".txt");
        await File.WriteAllTextAsync(pwfile, superPassword, ct);
        try
        {
            Run(Path.Combine(pgsql, "bin", "initdb.exe"), $"-D \"{pgdata}\" -U {Superuser} --pwfile=\"{pwfile}\" -A scram-sha-256 -E UTF8 --no-locale");
        }
        finally
        {
            File.Delete(pwfile);
        }

        await File.AppendAllTextAsync(Path.Combine(pgdata, "postgresql.conf"),
            string.Create(CultureInfo.InvariantCulture, $"{Environment.NewLine}# JISRN Forge{Environment.NewLine}port = {port}{Environment.NewLine}listen_addresses = 'localhost'{Environment.NewLine}max_connections = 200{Environment.NewLine}"), ct);
    }

    private async Task CreateForgeDatabaseAsync(int port, string superPassword, string appPassword, CancellationToken ct)
    {
        var admin = new NpgsqlConnectionStringBuilder { Host = "localhost", Port = port, Username = Superuser, Password = superPassword, Database = "postgres", Timeout = 30 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(admin.ConnectionString);
                await connection.OpenAsync(ct);
                await using (var role = new NpgsqlCommand($"CREATE ROLE {AppRole} LOGIN PASSWORD '{appPassword}'", connection)) await role.ExecuteNonQueryAsync(ct);
                await using (var db = new NpgsqlCommand($"CREATE DATABASE forge OWNER {AppRole} ENCODING 'UTF8'", connection)) await db.ExecuteNonQueryAsync(ct);
                break;
            }
            catch (NpgsqlException) when (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        // Extensions Forge's schema uses, created by the superuser so the Forge role does not need to be one.
        admin.Database = "forge";
        await using var forge = new NpgsqlConnection(admin.ConnectionString);
        await forge.OpenAsync(ct);
        await using var ext = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS btree_gist", forge);
        await ext.ExecuteNonQueryAsync(ct);
        Say("  database ready");
    }

    private static (string Pfx, string Cer, string Password) MakeCertificate(string folder)
    {
        var host = Environment.MachineName.ToLowerInvariant();
        string fqdn;
        try
        {
            fqdn = Dns.GetHostEntry(string.Empty).HostName.ToLowerInvariant();
        }
        catch (SocketException)
        {
            fqdn = host;
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={fqdn}, O=JISRN Forge", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var name in new[] { host, fqdn, "localhost" }.Distinct(StringComparer.Ordinal)) names.AddDnsName(name);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = TimeProvider.System.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
        var password = Secret(24);
        var pfx = Path.Combine(folder, "forge.pfx");
        var cer = Path.Combine(folder, "forge.cer");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
        File.WriteAllBytes(cer, certificate.Export(X509ContentType.Cert));
        return (pfx, cer, password);
    }

    private static string Settings(InstallOptions options, string data, string appPassword, string pfx, string certPassword, string codeFile)
    {
        var connection = new NpgsqlConnectionStringBuilder { Host = "localhost", Port = options.DatabasePort, Database = "forge", Username = AppRole, Password = appPassword, MaxPoolSize = 100 };
        var settings = new JsonObject
        {
            ["ConnectionStrings"] = new JsonObject { ["Forge"] = connection.ConnectionString },
            ["Kestrel"] = new JsonObject
            {
                ["Endpoints"] = new JsonObject
                {
                    ["Https"] = new JsonObject
                    {
                        ["Url"] = string.Create(CultureInfo.InvariantCulture, $"https://*:{options.WebPort}"),
                        ["Certificate"] = new JsonObject { ["Path"] = pfx, ["Password"] = certPassword },
                    },
                },
            },
            ["Forge"] = new JsonObject
            {
                ["Authentication"] = new JsonObject
                {
                    ["Mode"] = "Local",
                    ["Authority"] = "",
                    ["ClientId"] = "",
                    ["Local"] = new JsonObject { ["SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) },
                },
                ["Setup"] = new JsonObject { ["CodeFile"] = codeFile },
                ["Database"] = new JsonObject { ["MigrateOnStartup"] = true },
                ["Bootstrap"] = new JsonObject { ["AdminSubject"] = "", ["AdminDisplayName"] = "" },
                ["Secrets"] = new JsonObject { ["KeyFolder"] = Path.Combine(data, "keys") },
                ["Connectivity"] = new JsonObject { ["PkiFolder"] = Path.Combine(data, "pki") },
                ["Interchange"] = new JsonObject { ["Folder"] = Path.Combine(data, "interchange") },
            },
        };
        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void Register(InstallOptions options, string cer)
    {
        using (var key = Registry.LocalMachine.CreateSubKey(RegistryKey))
        {
            key.SetValue("InstallDirectory", options.InstallDirectory);
            key.SetValue("DataDirectory", options.DataDirectory);
            key.SetValue("Version", Version);
            key.SetValue("WebPort", options.WebPort, RegistryValueKind.DWord);
            key.SetValue("DatabasePort", options.DatabasePort, RegistryValueKind.DWord);
            key.SetValue("Certificate", cer);
        }

        using var uninstall = Registry.LocalMachine.CreateSubKey(UninstallKey);
        var setup = Path.Combine(options.InstallDirectory, "JISRN-Forge-Setup.exe");
        uninstall.SetValue("DisplayName", "JISRN Forge");
        uninstall.SetValue("DisplayVersion", Version);
        uninstall.SetValue("Publisher", "JIRSN");
        uninstall.SetValue("InstallLocation", options.InstallDirectory);
        uninstall.SetValue("DisplayIcon", setup);
        uninstall.SetValue("UninstallString", $"\"{setup}\" -uninstall");
        uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
        uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void Shortcut(int port)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "JISRN Forge.url");
        File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture, $"[InternetShortcut]{Environment.NewLine}URL=https://{Environment.MachineName.ToLowerInvariant()}:{port}/{Environment.NewLine}"));
    }

    private static async Task WaitReadyAsync(int port, string thumbprint, TimeSpan limit, CancellationToken ct, Process? process = null)
    {
        using var http = Client(thumbprint, port);
        var until = TimeProvider.System.GetUtcNow() + limit;
        while (TimeProvider.System.GetUtcNow() < until)
        {
            if (process is { HasExited: true }) throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Forge stopped while starting (exit code {process.ExitCode})."));
            try
            {
                if ((await http.GetAsync(new Uri("health/ready", UriKind.Relative), ct)).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        throw new TimeoutException("Forge did not become ready. Look in Event Viewer → Windows Logs → Application for 'JISRN Forge'.");
    }

    /// <summary>A client that trusts exactly this installation's certificate, for talking to Forge on this computer.</summary>
    private static HttpClient Client(string thumbprint, int port)
    {
#pragma warning disable CA2000 // the client owns and disposes the handler
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => string.Equals(certificate?.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase),
        };
#pragma warning restore CA2000
        return new HttpClient(handler, true) { BaseAddress = new Uri(string.Create(CultureInfo.InvariantCulture, $"https://localhost:{port}/")), Timeout = TimeSpan.FromSeconds(20) };
    }

    private void StartService(string name)
    {
        TryRun("sc.exe", $"start {name}");
        for (var i = 0; i < 60; i++)
        {
            if (Run("sc.exe", $"query {name}", quiet: true).Contains("RUNNING", StringComparison.Ordinal)) return;
            Thread.Sleep(1000);
        }

        throw new InvalidOperationException($"The {name} service did not start. Look in Event Viewer → Windows Logs → Application.");
    }

    private void StopService(string name)
    {
        if (!TryRun("sc.exe", $"query {name}")) return;
        TryRun("sc.exe", $"stop {name}");
        for (var i = 0; i < 60; i++)
        {
            if (Run("sc.exe", $"query {name}", quiet: true).Contains("STOPPED", StringComparison.Ordinal)) return;
            Thread.Sleep(1000);
        }
    }

    private static void PortFree(int port, string what)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Port {port} (for the {what}) is already in use. Choose another."));
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A random secret of letters and digits.</summary>
    private static string Secret(int length) => RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", length);

    /// <summary>A setup code that is easy to read out and type: three groups of four, no look-alike characters.</summary>
    private static string SetupCode()
    {
        var raw = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 12);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
    }

    private void Say(string message) => progress.Report(message);

    private string Run(string file, string arguments, bool quiet = false, bool capture = true)
    {
        if (!quiet) Say("  > " + Path.GetFileName(file) + " " + Redact(arguments));
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            RedirectStandardInput = !capture,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Could not run {file}.");
        if (!capture)
        {
            if (!process.WaitForExit(TimeSpan.FromMinutes(2))) throw new InvalidOperationException($"{Path.GetFileName(file)} did not finish.");
            return process.ExitCode == 0 ? string.Empty : throw new InvalidOperationException($"{Path.GetFileName(file)} failed ({process.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5))) throw new InvalidOperationException($"{Path.GetFileName(file)} did not finish.");
        process.WaitForExit();
        var text = output.Result + error.Result;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} failed ({process.ExitCode.ToString(CultureInfo.InvariantCulture)}): {text.Trim()}");
        return text;
    }

    private bool TryRun(string file, string arguments)
    {
        try
        {
            Run(file, arguments, quiet: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string Redact(string arguments) => arguments.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ? "(hidden)" : arguments;
}
