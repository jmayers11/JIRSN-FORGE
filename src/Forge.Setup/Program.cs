using System.Globalization;

namespace Forge.Setup;

/// <summary>
/// JISRN-Forge-Setup.exe: with no arguments, the setup wizard (install, or upgrade when Forge is already here).
/// -quiet installs or upgrades with the defaults and no window (for IT); -uninstall removes Forge (asking whether to keep the data;
/// -uninstall -quiet keeps it); -selftest FOLDER checks an install end to end in an empty folder without changing Windows.
/// Results of -quiet and -selftest go to setup.log beside the program and the exit code (0 = success).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var flags = args.Select(a => a.TrimStart('-', '/').ToLowerInvariant()).ToList();
        var quiet = flags.Contains("quiet");
        if (flags.Contains("selftest"))
        {
            var index = flags.IndexOf("selftest");
            var folder = index + 1 < args.Length ? args[index + 1] : Path.Combine(Path.GetTempPath(), "forge-selftest");
            return Console(engine => engine.SelfTestAsync(folder, CancellationToken.None).GetAwaiter().GetResult());
        }

        if (flags.Contains("uninstall"))
        {
            if (quiet) return Console(engine => { engine.Uninstall(false); return true; });
            ApplicationConfiguration.Initialize();
            if (MessageBox.Show("Remove JISRN Forge from this computer?", "JISRN Forge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
            var deleteData = MessageBox.Show(
                "Also delete Forge's database, settings and certificate?\n\nChoose No to keep them (you can reinstall later and carry on). Choose Yes only if you have a backup or no longer need the records.",
                "JISRN Forge", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            try
            {
                var log = new List<string>();
                new SetupEngine(new Progress<string>(log.Add)).Uninstall(deleteData);
                MessageBox.Show(deleteData ? "JISRN Forge and its data were removed." : "JISRN Forge was removed. Its data is kept.", "JISRN Forge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(ex.Message, "JISRN Forge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        if (quiet)
            return Console(engine =>
            {
                var result = engine.InstallAsync(InstallOptions.Defaults, CancellationToken.None).GetAwaiter().GetResult();
                return result.Url.Length > 0;
            });

        ApplicationConfiguration.Initialize();
        Application.Run(new Wizard());
        return 0;
    }

    /// <summary>Runs without a window, writing every step to setup.log beside the program.</summary>
    private static int Console(Func<SetupEngine, bool> work)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "setup.log");
        using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        var progress = new SynchronousProgress(line => log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{TimeProvider.System.GetLocalNow():yyyy-MM-dd HH:mm:ss} {line}")));
        try
        {
            return work(new SetupEngine(progress)) ? 0 : 1;
        }
#pragma warning disable CA1031 // a console run reports every failure in its log and exit code
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.WriteLine("FAILED: " + ex);
            return 1;
        }
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
