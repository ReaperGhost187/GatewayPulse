using System.Diagnostics;
using System.Drawing;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Forms;

namespace GatewayPulse.Tray;

public sealed class TrayContext : ApplicationContext
{
    private const string DashboardUrl = "http://127.0.0.1:8080/";
    private const string NetworkMapUrl = "http://127.0.0.1:8080/network-map.html";
    private const string SettingsUrl = "http://127.0.0.1:8080/settings.html";
    private const string StatusUrl = "http://127.0.0.1:8080/api/status";
    private const string TestAlertUrl = "http://127.0.0.1:8080/api/testalert";
    private const string ServiceName = "GatewayPulse";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private NetworkMapForm? _networkMapForm;

    public TrayContext()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Gateway Pulse - Read-only Gateway Monitor",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _pollTimer = new System.Windows.Forms.Timer
        {
            Interval = 5000
        };
        _pollTimer.Tick += async (_, _) => await PollStatusAsync();
        _pollTimer.Start();

        _ = PollStatusAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => OpenUrl(DashboardUrl));
        menu.Items.Add("Network Map", null, (_, _) => OpenNetworkMap());
        menu.Items.Add("Settings", null, (_, _) => OpenUrl(SettingsUrl));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Send Test Alert", null, async (_, _) => await SendTestAlertAsync());
        menu.Items.Add("Restart Service", null, async (_, _) => await RestartServiceAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Tray", null, (_, _) => ExitTray());
        return menu;
    }

    private static Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "GatewayPulse.ico");
        if (File.Exists(iconPath))
            return new Icon(iconPath);

        var pngPath = Path.Combine(AppContext.BaseDirectory, "GatewayPulse.png");
        if (File.Exists(pngPath))
        {
            using var bitmap = new Bitmap(pngPath);
            return Icon.FromHandle(bitmap.GetHicon());
        }

        return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
    }

    private void OpenNetworkMap()
    {
        try
        {
            if (_networkMapForm is null || _networkMapForm.IsDisposed)
                _networkMapForm = new NetworkMapForm();

            if (_networkMapForm.Visible)
            {
                _networkMapForm.Activate();
                return;
            }

            _networkMapForm.Show();
            _networkMapForm.Activate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Unable to open the Network Map window.\n\n{ex.Message}\n\nOpening the browser page instead.",
                "Gateway Pulse",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            OpenUrl(NetworkMapUrl);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Unable to open {url}\n\n{ex.Message}",
                "Gateway Pulse",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task SendTestAlertAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync(TestAlertUrl, content: null);
            var ok = response.IsSuccessStatusCode && await ReadOkResultAsync(response);

            ShowTestResult(ok);
        }
        catch
        {
            ShowTestResult(false);
        }
    }

    private static async Task<bool> ReadOkResultAsync(HttpResponseMessage response)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return json.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private void ShowTestResult(bool ok)
    {
        _notifyIcon.ShowBalloonTip(
            3000,
            "Gateway Pulse",
            ok ? "Gateway Pulse test alert sent" : "Gateway Pulse test alert failed",
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private async Task RestartServiceAsync()
    {
        try
        {
            var stop = await RunScAsync("stop", ServiceName);
            if (IsPermissionFailure(stop))
            {
                ShowRestartPermissionMessage();
                return;
            }

            await Task.Delay(2500);

            var start = await RunScAsync("start", ServiceName);
            if (IsPermissionFailure(start))
            {
                ShowRestartPermissionMessage();
                return;
            }

            if (start.ExitCode != 0)
            {
                MessageBox.Show(
                    "Gateway Pulse service restart failed.",
                    "Gateway Pulse",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _notifyIcon.ShowBalloonTip(
                3000,
                "Gateway Pulse",
                "Gateway Pulse service restarted",
                ToolTipIcon.Info);
        }
        catch
        {
            ShowRestartPermissionMessage();
        }
    }

    private static async Task<CommandResult> RunScAsync(string action, string serviceName)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"{action} {serviceName}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (process is null)
            return new CommandResult(-1, "", "Unable to start sc.exe");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandResult(process.ExitCode, output, error);
    }

    private static bool IsPermissionFailure(CommandResult result)
    {
        var combined = $"{result.Output}\n{result.Error}";
        return result.ExitCode == 5 ||
               combined.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("FAILED 5", StringComparison.OrdinalIgnoreCase);
    }

    private static void ShowRestartPermissionMessage()
    {
        MessageBox.Show(
            "Administrator permission is required to restart the Gateway Pulse service.",
            "Gateway Pulse",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private async Task PollStatusAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync(StatusUrl);
            if (!response.IsSuccessStatusCode)
            {
                SetTooltip("Gateway Pulse - Service Not Responding");
                return;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var relay = ReadBool(json, "relayRunning") ? "Running" : "Stopped";
            var trimode = ReadBool(json, "trimodeSeen") ? "Running" : "Stopped";
            var scanner = ReadBool(json, "scannerEnabled") ? "Running" : "Stopped";

            SetTooltip($"Gateway Pulse\nRelay: {relay}\nTrimode: {trimode}\nScanner: {scanner}");
        }
        catch
        {
            SetTooltip("Gateway Pulse - Service Not Responding");
        }
    }

    private static bool ReadBool(JsonElement json, string propertyName)
    {
        return json.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private void SetTooltip(string text)
    {
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void ExitTray()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _httpClient.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
            _notifyIcon.Dispose();
            _httpClient.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
