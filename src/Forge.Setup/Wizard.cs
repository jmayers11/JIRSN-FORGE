using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace Forge.Setup;

/// <summary>The setup wizard: welcome, choices (new installs only), progress, and the address and setup code at the end.</summary>
internal sealed class Wizard : Form
{
    private static readonly Color Graphite = Color.FromArgb(0x1E, 0x22, 0x27);
    private static readonly Color Orange = Color.FromArgb(0xF9, 0x73, 0x16);
    private static readonly Color Light = Color.FromArgb(0xE5, 0xE7, 0xEB);

    private readonly Panel body = new() { Dock = DockStyle.Fill, Padding = new Padding(24) };
    private readonly Button back = new() { Text = "< Back", Width = 90 };
    private readonly Button next = new() { Text = "Next >", Width = 90 };
    private readonly Button cancel = new() { Text = "Cancel", Width = 90 };
    private readonly ExistingInstall? existing = SetupEngine.FindExisting();
    private readonly TextBox installDir = new() { Width = 420 };
    private readonly TextBox dataDir = new() { Width = 420 };
    private readonly NumericUpDown webPort = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly CheckBox firewall = new() { AutoSize = true, Text = "Let other computers on the network open Forge (adds a Windows Firewall rule)" };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    private int page;
    private bool busy;
    private InstallResult? result;

    public Wizard()
    {
        Text = "JISRN Forge Setup " + SetupEngine.Version;
        ClientSize = new Size(640, 460);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        var defaults = InstallOptions.Defaults;
        installDir.Text = defaults.InstallDirectory;
        dataDir.Text = defaults.DataDirectory;
        webPort.Value = defaults.WebPort;
        firewall.Checked = defaults.OpenFirewall;

        var banner = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Graphite };
        banner.Controls.Add(new Label { Text = "JISRN FORGE", ForeColor = Orange, Font = new Font("Segoe UI", 18f, FontStyle.Bold), AutoSize = true, Location = new Point(20, 8) });
        banner.Controls.Add(new Label { Text = "Manufacturing operations · server setup", ForeColor = Light, AutoSize = true, Location = new Point(22, 40) });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.AddRange([cancel, next, back]);
        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(banner);
        back.Click += (_, _) => Go(page - 1);
        next.Click += async (_, _) => await NextAsync();
        cancel.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (busy) e.Cancel = true;
        };
        Go(0);
    }

    private void Go(int target)
    {
        page = target;
        body.Controls.Clear();
        back.Visible = page is 1;
        cancel.Visible = page is 0 or 1;
        next.Enabled = true;
        switch (page)
        {
            case 0:
                Add(Heading(existing is null ? "Install JISRN Forge" : "Upgrade JISRN Forge"), 0);
                Add(Words(existing is null
                    ? "This installs Forge on this computer as a server for your plant. People open it in a web browser from any PC, tablet or kiosk on your network.\n\n"
                      + "It installs:\n  • Forge, as a Windows service that starts with Windows\n  • its own PostgreSQL database, reachable only from this computer\n  • an HTTPS certificate for this computer's name\n\n"
                      + "After setup you create the first administrator in the browser with a one-time setup code."
                    : $"Forge {existing.Version} is installed here. This upgrades it to {SetupEngine.Version}.\n\nForge stops for a few minutes. Your database, settings, accounts and certificate are kept, and the database is updated when Forge starts again.\n\nBack up the database first if you have records you cannot lose."), 36);
                next.Text = existing is null ? "Next >" : "Upgrade";
                break;
            case 1:
                Add(Heading("Where and how"), 0);
                Add(Words("Program folder"), 40);
                Add(installDir, 62);
                Add(Words("Data folder (database, settings, certificate). Back this folder up."), 98);
                Add(dataDir, 120);
                Add(Words("Web port (people open https://this-computer:PORT/)"), 156);
                Add(webPort, 178);
                Add(firewall, 216);
                next.Text = "Install";
                break;
            case 2:
                Add(Heading(existing is null ? "Installing…" : "Upgrading…"), 0);
                var frame = new Panel { Location = new Point(0, 36), Size = new Size(592, 260) };
                frame.Controls.Add(log);
                body.Controls.Add(frame);
                next.Enabled = false;
                next.Text = "Next >";
                break;
            default:
                Done();
                break;
        }
    }

    private async Task NextAsync()
    {
        if (page == 0 && existing is null)
        {
            Go(1);
            return;
        }

        if (page is 0 or 1)
        {
            var options = new InstallOptions(installDir.Text.Trim(), dataDir.Text.Trim(), (int)webPort.Value, InstallOptions.Defaults.DatabasePort, firewall.Checked);
            Go(2);
            busy = true;
            var progress = new Progress<string>(line => log.AppendText(line + Environment.NewLine));
            try
            {
                result = await Task.Run(() => new SetupEngine(progress).InstallAsync(options, CancellationToken.None));
                busy = false;
                Go(3);
            }
#pragma warning disable CA1031 // the wizard shows any failure to the person installing
            catch (Exception ex)
#pragma warning restore CA1031
            {
                busy = false;
                log.AppendText(Environment.NewLine + "SETUP STOPPED: " + ex.Message + Environment.NewLine);
                MessageBox.Show(this, ex.Message + "\n\nThe steps so far are listed in the window. Fix the problem, then run setup again.", "JISRN Forge Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                next.Text = "Close";
                next.Enabled = true;
                cancel.Visible = false;
                page = 4;
            }

            return;
        }

        Close();
    }

    private void Done()
    {
        var r = result!;
        Add(Heading(r.Upgraded ? "Forge is upgraded" : "Forge is installed"), 0);
        Add(Words("Open Forge in a browser at:"), 40);
        var link = new LinkLabel { Text = r.Url, AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold) };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(r.Url) { UseShellExecute = true });
        Add(link, 62);
        var y = 100;
        if (r.SetupCode is { } code)
        {
            Add(Words("Setup code for the first administrator (also in setup-code.txt in the data folder):"), y);
            var box = new TextBox { Text = code, ReadOnly = true, Width = 240, Font = new Font(FontFamily.GenericMonospace, 16f, FontStyle.Bold) };
            Add(box, y + 22);
            var copy = new Button { Text = "Copy", Width = 70, Location = new Point(256, y + 24) };
            copy.Click += (_, _) => Clipboard.SetText(code);
            body.Controls.Add(copy);
            y += 70;
        }

        Add(Words(string.Create(CultureInfo.InvariantCulture,
            $"The first time each browser opens Forge it warns that the certificate is not trusted: it was made on this computer. To remove the warning, have IT trust {Path.GetFileName(r.CertificatePath)} on your PCs (it is in {Path.GetDirectoryName(r.CertificatePath)}), or replace it with a company certificate.")), y);
        next.Text = "Finish";
        next.Enabled = true;
        back.Visible = false;
        cancel.Visible = false;
    }

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 13f, FontStyle.Bold) };

    private static Label Words(string text) => new() { Text = text, MaximumSize = new Size(590, 0), AutoSize = true };

    private void Add(Control control, int y)
    {
        control.Location = new Point(0, y);
        body.Controls.Add(control);
    }
}
