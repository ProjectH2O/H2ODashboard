using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace H2ODashboard
{
    /// <summary>Control window: feed status, skin/background choice, browser and OBS links.</summary>
    public sealed class MainForm : Form
    {
        readonly FeedPoller _poller;
        readonly DashboardServer _server;
        readonly Settings _settings;

        readonly Label _status = new Label { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        readonly Label _live = new Label { AutoSize = true };
        readonly ComboBox _skin = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        readonly ComboBox _background = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        readonly TextBox _keyColour = new TextBox { Width = 80 };
        readonly TextBox _url = new TextBox { ReadOnly = true, Width = 420 };
        readonly Label _size = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 0) };
        readonly Timer _timer = new Timer { Interval = 250 };
        readonly Button _overlayButton = new Button { Text = "Show overlay", AutoSize = true };
        readonly Button _editButton = new Button { Text = "Edit overlays (Ctrl+Shift+D)", AutoSize = true, Enabled = false };
        readonly string _webViewData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2ODashboard", "WebView2");
        readonly Dictionary<string, SkinWindow> _windows = new Dictionary<string, SkinWindow>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, OverlayWindow> _overlays = new Dictionary<string, OverlayWindow>(StringComparer.OrdinalIgnoreCase);
        bool _closing;

        static readonly string[] Backgrounds = { "transparent", "key", "panel" };
        const int HotkeyId = 1, WM_HOTKEY = 0x312, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;

        public MainForm(FeedPoller poller, DashboardServer server, Settings settings)
        {
            _poller = poller;
            _server = server;
            _settings = settings;

            Text = "H2Overdrive Dashboard";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(500, 390);
            StartPosition = FormStartPosition.CenterScreen;

            var openBrowser = new Button { Text = "Open in browser", AutoSize = true };
            var copyUrl = new Button { Text = "Copy OBS URL", AutoSize = true };
            openBrowser.Click += (s, e) => Process.Start(_url.Text);
            copyUrl.Click += (s, e) => Clipboard.SetText(_url.Text);

            _skin.Items.AddRange(_server.Skins.Cast<object>().ToArray());
            _skin.SelectedItem = _skin.Items.Contains(_settings.Skin) ? _settings.Skin : _skin.Items.Cast<object>().FirstOrDefault();
            _background.Items.AddRange(Backgrounds);
            _background.SelectedItem = Backgrounds.Contains(_settings.Background) ? _settings.Background : "transparent";
            _keyColour.Text = _settings.KeyColour;

            _skin.SelectedIndexChanged += (s, e) => UpdateUrl();
            _background.SelectedIndexChanged += (s, e) => UpdateUrl();
            _keyColour.TextChanged += (s, e) => UpdateUrl();

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, AutoSize = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Row(string label, Control control)
            {
                layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
                layout.Controls.Add(control);
            }
            layout.Controls.Add(_status);
            layout.SetColumnSpan(_status, 2);
            layout.Controls.Add(_live);
            layout.SetColumnSpan(_live, 2);
            Row("Skin", _skin);
            Row("Background", _background);
            Row("Key colour", _keyColour);
            Row("OBS URL", _url);
            Row("OBS size", _size);
            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.AddRange(new Control[] { openBrowser, copyUrl });
            layout.Controls.Add(new Label());
            layout.Controls.Add(buttons);

            var showWindow = new Button { Text = "Show window", AutoSize = true };
            showWindow.Click += (s, e) => { if (_skin.SelectedItem is string skin) ShowSkinWindow(skin); };
            _overlayButton.Click += (s, e) => { if (_skin.SelectedItem is string skin) SetOverlay(skin, !_overlays.ContainsKey(skin)); };
            _editButton.Click += (s, e) => ToggleEdit();
            var windows = new FlowLayoutPanel { AutoSize = true };
            windows.Controls.AddRange(new Control[] { showWindow, _overlayButton, _editButton });
            layout.Controls.Add(new Label { Text = "Windows", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
            layout.Controls.Add(windows);

            var hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(470, 0),
                ForeColor = SystemColors.GrayText,
                Text = "OBS: add one Browser Source per skin with its URL (transparent background works directly). " +
                       "Use 'key' with a Colour Key filter for window capture.\n" +
                       "Windows and overlays are per skin: pick a skin, then Show window / Show overlay; several can be open at once. " +
                       "Overlays are transparent and click-through over the game (Borderless/Windowed mode). " +
                       "Ctrl+Shift+D toggles edit mode on all overlays: drag to move, drag the edges to resize.",
            };
            layout.Controls.Add(hint);
            layout.SetColumnSpan(hint, 2);
            Controls.Add(layout);

            UpdateUrl();
            _timer.Tick += (s, e) => UpdateStatus();
            _timer.Start();
            UpdateStatus();
            var skins = _server.Skins.ToList();
            var reopen = _settings.Overlays.Where(o => skins.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList();
            Shown += (s, e) => reopen.ForEach(skin => SetOverlay(skin, true));
        }

        string SkinUrl(string skin)
        {
            var background = _background.SelectedItem as string ?? "transparent";
            var url = _server.SkinUrl(skin, background);
            if (background == "key")
                url += "&key=" + _keyColour.Text.Trim().TrimStart('#');
            return url;
        }

        void ShowSkinWindow(string skin)
        {
            if (_windows.TryGetValue(skin, out var existing))
            {
                existing.Activate();
                return;
            }
            var window = new SkinWindow(() => SkinUrl(skin), _webViewData, _settings.GetWindowBounds(skin));
            window.Text = "H2Overdrive Dashboard - " + skin; // distinct titles for OBS Window Capture
            window.FormClosed += (s, e) =>
            {
                _settings.SkinWindowBounds[skin] = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;
                _settings.Save();
                _windows.Remove(skin);
            };
            _windows[skin] = window;
            window.Show();
        }

        void SetOverlay(string skin, bool visible)
        {
            if (visible && !_overlays.ContainsKey(skin))
            {
                var overlay = new OverlayWindow(() => _server.SkinUrl(skin, "transparent"), _webViewData, _settings.GetOverlayBounds(skin));
                overlay.Text = "H2Overdrive Dashboard Overlay - " + skin;
                overlay.BoundsSaved += () => SaveOverlayBounds(skin, overlay);
                overlay.FormClosed += (s, e) =>
                {
                    SaveOverlayBounds(skin, overlay);
                    _overlays.Remove(skin);
                    if (!_closing) // on exit, keep it listed so it reopens next time
                        _settings.Overlays.Remove(skin);
                    _settings.Save();
                    UpdateButtons();
                };
                _overlays[skin] = overlay;
                overlay.Show();
                _settings.Overlays.Add(skin);
                _settings.Save();
            }
            else if (!visible && _overlays.TryGetValue(skin, out var open))
            {
                open.Close();
            }
            UpdateButtons();
        }

        void SaveOverlayBounds(string skin, OverlayWindow overlay)
        {
            _settings.SkinOverlayBounds[skin] = overlay.Bounds;
            _settings.Save();
        }

        void UpdateButtons()
        {
            bool open = _skin.SelectedItem is string skin && _overlays.ContainsKey(skin);
            _overlayButton.Text = open ? "Hide overlay" : "Show overlay";
            _editButton.Enabled = _overlays.Count > 0;
            _editButton.Text = _overlays.Values.Any(o => o.Editing) ? "Lock overlays (Ctrl+Shift+D)" : "Edit overlays (Ctrl+Shift+D)";
        }

        void ToggleEdit()
        {
            if (_overlays.Count == 0 && _skin.SelectedItem is string skin)
                SetOverlay(skin, true);
            bool editing = !_overlays.Values.Any(o => o.Editing);
            foreach (var overlay in _overlays.Values)
                overlay.Editing = editing;
            UpdateButtons();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Works while the game has focus
            RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (int)Keys.D);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId)
                ToggleEdit();
            base.WndProc(ref m);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int key);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        void UpdateUrl()
        {
            var skin = _skin.SelectedItem as string;
            var background = _background.SelectedItem as string ?? "transparent";
            _keyColour.Enabled = background == "key";
            if (skin == null)
            {
                _url.Text = $"http://127.0.0.1:{_server.Port}/";
                return;
            }
            _url.Text = SkinUrl(skin);
            var size = _server.SkinSize(skin);
            _size.Text = size == null
                ? "any (the skin fills the source)"
                : $"{size.Replace("x", " x ")} (or any size with that shape; the skin scales to fit)";

            _settings.Skin = skin;
            _settings.Background = background;
            _settings.KeyColour = _keyColour.Text.Trim();
            _settings.Save();
            foreach (var window in _windows.Values)
                window.Navigate(); // the background may have changed
            UpdateButtons();
        }

        void UpdateStatus()
        {
            var s = _poller.Latest;
            if (!_poller.Connected || s == null)
            {
                _status.Text = "Waiting for H2Overdrive...";
                _status.ForeColor = Color.DarkOrange;
                _live.Text = "Start the game from TeknoParrot (our OpenParrot build publishes the data).";
                return;
            }
            _status.Text = s.InRace ? "Game connected - in race" : "Game connected";
            _status.ForeColor = Color.ForestGreen;
            var c = CultureInfo.InvariantCulture;
            _live.Text = s.InRace
                ? string.Format(c, "Speed {0:0.0} {1} ({2:0} units/s)   Boost {3:0.0}/12   Dashboard [{4}]",
                    s.GameSpeed, s.Metric ? "KPH" : "MPH", s.Speed, s.Boost, s.DigitText)
                : string.Format(c, "Dashboard [{0}]", s.DigitText);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            UnregisterHotKey(Handle, HotkeyId);
            _closing = true;
            foreach (var window in _windows.Values.ToList())
                window.Close();
            foreach (var overlay in _overlays.Values.ToList())
                overlay.Close();
            _settings.Save();
            base.OnFormClosed(e);
        }
    }
}
