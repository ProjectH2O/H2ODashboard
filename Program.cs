using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace H2ODashboard
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Physical pixels everywhere, so the overlay lines up with the game
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } // per-monitor v2 (Windows 10 1703+)
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var settings = Settings.Load(Path.Combine(baseDir, "H2ODashboard.ini"));

            // --port=N overrides the saved port for this run only (not written back to the ini)
            int listenPort = settings.Port;
            var portArg = args.FirstOrDefault(a => a.StartsWith("--port="));
            if (portArg != null && int.TryParse(portArg.Substring("--port=".Length), out var port))
                listenPort = port;

            using (var poller = new FeedPoller())
            {
                DashboardServer server;
                try
                {
                    server = new DashboardServer(poller, baseDir, listenPort);
                }
                catch (SocketException e)
                {
                    MessageBox.Show($"Can't listen on 127.0.0.1:{listenPort} ({e.Message}).\n\n" +
                                    "Is the dashboard already running? Otherwise pick another port with --port=N.",
                        "H2Overdrive Dashboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                using (server)
                    Application.Run(new MainForm(poller, server, settings));
            }
            return 0;
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }

    /// <summary>Plain key=value settings next to the exe.</summary>
    public sealed class Settings
    {
        string _path;
        public int Port = 8765;
        public string Skin = "cabinet";
        public string Background = "transparent";
        public string KeyColour = "00ff00";
        // Each skin has its own window and overlay (several can be open at once)
        public Rectangle WindowBounds = new Rectangle(100, 100, 960, 480);   // default for skins without saved bounds
        public Rectangle OverlayBounds = new Rectangle(40, 40, 480, 220);
        public readonly Dictionary<string, Rectangle> SkinWindowBounds = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Rectangle> SkinOverlayBounds = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Overlays = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // reopened at start

        public Rectangle GetWindowBounds(string skin) => SkinWindowBounds.TryGetValue(skin, out var r) ? r : WindowBounds;
        public Rectangle GetOverlayBounds(string skin) => SkinOverlayBounds.TryGetValue(skin, out var r) ? r : OverlayBounds;

        public static Settings Load(string path)
        {
            var settings = new Settings { _path = path };
            if (!File.Exists(path))
                return settings;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            if (values.TryGetValue("Port", out var port) && int.TryParse(port, out var p))
                settings.Port = p;
            if (values.TryGetValue("Skin", out var skin))
                settings.Skin = skin;
            if (values.TryGetValue("Background", out var background))
                settings.Background = background;
            if (values.TryGetValue("KeyColour", out var key))
                settings.KeyColour = key;
            if (values.TryGetValue("WindowBounds", out var wb) && TryParseRect(wb, out var w))
                settings.WindowBounds = w;
            if (values.TryGetValue("OverlayBounds", out var ob) && TryParseRect(ob, out var o))
                settings.OverlayBounds = o;
            // Old single-overlay setting: reopen it for the saved skin
            if (values.TryGetValue("OverlayVisible", out var ov) && ov == "1")
                settings.Overlays.Add(settings.Skin);
            if (values.TryGetValue("Overlays", out var list))
                foreach (var name in list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    settings.Overlays.Add(name.Trim());
            foreach (var kv in values)
            {
                if (kv.Key.StartsWith("WindowBounds.", StringComparison.OrdinalIgnoreCase) && TryParseRect(kv.Value, out var sw))
                    settings.SkinWindowBounds[kv.Key.Substring("WindowBounds.".Length)] = sw;
                else if (kv.Key.StartsWith("OverlayBounds.", StringComparison.OrdinalIgnoreCase) && TryParseRect(kv.Value, out var so))
                    settings.SkinOverlayBounds[kv.Key.Substring("OverlayBounds.".Length)] = so;
            }
            return settings;
        }

        public void Save()
        {
            try
            {
                var text = new System.Text.StringBuilder();
                text.Append($"Port={Port}\nSkin={Skin}\nBackground={Background}\nKeyColour={KeyColour}\n");
                text.Append($"WindowBounds={Rect(WindowBounds)}\nOverlayBounds={Rect(OverlayBounds)}\n");
                text.Append($"Overlays={string.Join(",", Overlays)}\n");
                foreach (var kv in SkinWindowBounds)
                    text.Append($"WindowBounds.{kv.Key}={Rect(kv.Value)}\n");
                foreach (var kv in SkinOverlayBounds)
                    text.Append($"OverlayBounds.{kv.Key}={Rect(kv.Value)}\n");
                File.WriteAllText(_path, text.ToString());
            }
            catch (IOException)
            {
                // read-only folder: settings just aren't remembered
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static string Rect(Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

        static bool TryParseRect(string text, out Rectangle rect)
        {
            rect = default;
            var parts = text.Split(',');
            if (parts.Length != 4 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y) ||
                !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h) || w < 40 || h < 20)
                return false;
            rect = new Rectangle(x, y, w, h);
            return true;
        }
    }
}
