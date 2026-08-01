using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GatewayPulse.Tray;

public sealed class NetworkMapForm : Form
{
    private const string NetworkMapApiUrl = "http://127.0.0.1:8080/api/network-map";
    private const string DefaultMapUrl = "https://cms.winlink.org:444/maps/WinlinkGateways.aspx";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Label _statusLabel;
    private readonly Button _refreshButton;
    private readonly Button _openBrowserButton;
    private readonly WebView2 _webView;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private string _currentUrl = DefaultMapUrl;
    private bool _autoRefresh;
    private int _autoRefreshMinutes = 15;

    public NetworkMapForm()
    {
        Text = "Gateway Pulse — Network Map";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1280, 800);
        BackColor = Color.FromArgb(0, 2, 6);
        ForeColor = Color.FromArgb(238, 244, 251);
        Font = new Font("Segoe UI", 10f, FontStyle.Regular);

        var toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Color.FromArgb(5, 10, 16),
            Padding = new Padding(14, 10, 14, 10)
        };

        _statusLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(140, 162, 184),
            Text = "Loading Network Map..."
        };

        var buttonHost = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        _refreshButton = CreateToolbarButton("Refresh");
        _refreshButton.Click += async (_, _) => await LoadMapAsync(forceReload: true);

        _openBrowserButton = CreateToolbarButton("Open in Browser");
        _openBrowserButton.Click += (_, _) => OpenInBrowser(_currentUrl);

        buttonHost.Controls.Add(_refreshButton);
        buttonHost.Controls.Add(_openBrowserButton);
        toolbar.Controls.Add(_statusLabel);
        toolbar.Controls.Add(buttonHost);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };

        _refreshTimer = new System.Windows.Forms.Timer();
        _refreshTimer.Tick += async (_, _) => await LoadMapAsync(forceReload: true);

        Controls.Add(_webView);
        Controls.Add(toolbar);

        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _httpClient.Dispose();
        };
    }

    private static Button CreateToolbarButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(20, 40, 58),
        ForeColor = Color.FromArgb(238, 244, 251),
        Margin = new Padding(8, 4, 0, 4),
        Padding = new Padding(12, 6, 12, 6),
        Cursor = Cursors.Hand
    };

    private async Task InitializeAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            await LoadMapAsync(forceReload: false);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "WebView2 runtime is required for the embedded map.";
            var result = MessageBox.Show(
                this,
                "The embedded Network Map needs the Microsoft Edge WebView2 Runtime.\n\n" +
                "Open the Winlink map in your default browser instead?\n\n" +
                ex.Message,
                "Gateway Pulse Network Map",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result == DialogResult.Yes)
            {
                await LoadMapAsync(forceReload: false);
                OpenInBrowser(_currentUrl);
            }
        }
    }

    private async Task LoadMapAsync(bool forceReload)
    {
        try
        {
            using var response = await _httpClient.GetAsync(NetworkMapApiUrl);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;

            var serviceCode = ReadString(root, "serviceCode", "ServiceCode");
            var mapUrl = ReadString(root, "mapUrl", "MapUrl");
            var resolved = ReadString(root, "resolvedMapUrl", "ResolvedMapUrl");
            _autoRefresh = ReadBool(root, "autoRefresh", "AutoRefresh", true);
            _autoRefreshMinutes = ReadInt(root, "autoRefreshMinutes", "AutoRefreshMinutes", 15);

            _currentUrl = string.IsNullOrWhiteSpace(resolved)
                ? (string.IsNullOrWhiteSpace(mapUrl) ? DefaultMapUrl : mapUrl)
                : resolved;

            var codeLabel = string.IsNullOrWhiteSpace(serviceCode) ? "(none)" : serviceCode;
            var refreshLabel = _autoRefresh
                ? $"auto-refresh every {_autoRefreshMinutes} min"
                : "auto-refresh off";
            _statusLabel.Text = $"Service code: {codeLabel}  ·  {refreshLabel}";

            if (_webView.CoreWebView2 is not null)
            {
                if (forceReload)
                    _webView.CoreWebView2.Navigate(_currentUrl);
                else if (!string.Equals(_webView.Source?.AbsoluteUri, _currentUrl, StringComparison.OrdinalIgnoreCase))
                    _webView.Source = new Uri(_currentUrl);
            }

            ConfigureRefreshTimer();
        }
        catch (Exception ex)
        {
            _currentUrl = DefaultMapUrl;
            _statusLabel.Text = $"Unable to load settings from Gateway Pulse service. Using default map. ({ex.Message})";
            if (_webView.CoreWebView2 is not null)
                _webView.Source = new Uri(_currentUrl);
            ConfigureRefreshTimer();
        }
    }

    private void ConfigureRefreshTimer()
    {
        _refreshTimer.Stop();
        if (!_autoRefresh)
            return;
        _refreshTimer.Interval = Math.Max(1, _autoRefreshMinutes) * 60 * 1000;
        _refreshTimer.Start();
    }

    private static void OpenInBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static string ReadString(JsonElement root, string camel, string pascal)
    {
        if (root.TryGetProperty(camel, out var camelValue) && camelValue.ValueKind == JsonValueKind.String)
            return camelValue.GetString() ?? "";
        if (root.TryGetProperty(pascal, out var pascalValue) && pascalValue.ValueKind == JsonValueKind.String)
            return pascalValue.GetString() ?? "";
        return "";
    }

    private static bool ReadBool(JsonElement root, string camel, string pascal, bool fallback)
    {
        if (root.TryGetProperty(camel, out var camelValue) &&
            (camelValue.ValueKind == JsonValueKind.True || camelValue.ValueKind == JsonValueKind.False))
            return camelValue.GetBoolean();
        if (root.TryGetProperty(pascal, out var pascalValue) &&
            (pascalValue.ValueKind == JsonValueKind.True || pascalValue.ValueKind == JsonValueKind.False))
            return pascalValue.GetBoolean();
        return fallback;
    }

    private static int ReadInt(JsonElement root, string camel, string pascal, int fallback)
    {
        if (root.TryGetProperty(camel, out var camelValue) && camelValue.TryGetInt32(out var camelInt))
            return camelInt;
        if (root.TryGetProperty(pascal, out var pascalValue) && pascalValue.TryGetInt32(out var pascalInt))
            return pascalInt;
        return fallback;
    }
}
