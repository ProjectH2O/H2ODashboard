using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace H2ODashboard
{
    /// <summary>Polls the shared-memory feed on one thread and keeps the latest frame as JSON.</summary>
    public sealed class FeedPoller : IDisposable
    {
        readonly DashboardFeed _feed = new DashboardFeed();
        readonly CourseStore _courses = new CourseStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "H2ODashboard.courses.ini"));
        readonly TimeAttackStore _timeAttack = new TimeAttackStore(AppDomain.CurrentDomain.BaseDirectory);
        readonly Thread _thread;
        volatile bool _stop;

        public volatile string Json = new DashboardSnapshot().ToJson(false);
        public volatile DashboardSnapshot Latest;
        public volatile bool Connected;
        /// <summary>Changes whenever Json changes (frame or connection state).</summary>
        public long Version;

        public FeedPoller()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "FeedPoller" };
            _thread.Start();
        }

        void Run()
        {
            uint lastFrame = uint.MaxValue;
            bool lastConnected = false;
            while (!_stop)
            {
                var snapshot = _feed.Read();
                bool connected = snapshot != null && _feed.Connected;
                if (snapshot == null)
                    snapshot = new DashboardSnapshot();
                _courses.Observe(snapshot);
                snapshot.CourseCheckpoints = _courses.Get(snapshot.Track);
                _timeAttack.Observe(snapshot);

                if (snapshot.Frame != lastFrame || connected != lastConnected)
                {
                    lastFrame = snapshot.Frame;
                    lastConnected = connected;
                    Latest = snapshot;
                    Connected = connected;
                    Json = snapshot.ToJson(connected);
                    Interlocked.Increment(ref Version);
                }
                Thread.Sleep(connected ? 2 : 250);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(1000);
            _feed.Dispose();
        }
    }

    /// <summary>
    /// Minimal HTTP server on 127.0.0.1 (no URL ACL / admin needed):
    ///   /                  skin list
    ///   /h2o.js, /h2o.css  skin API
    ///   /skins/name/...    skin files
    ///   /data              Server-Sent Events stream of frames (JSON)
    ///   /data.json         latest frame
    /// </summary>
    public sealed class DashboardServer : IDisposable
    {
        readonly FeedPoller _poller;
        readonly string _wwwRoot, _skinsRoot;
        readonly TcpListener _listener;
        volatile bool _stop;

        public int Port { get; }

        public DashboardServer(FeedPoller poller, string baseDir, int port)
        {
            _poller = poller;
            _wwwRoot = Path.GetFullPath(Path.Combine(baseDir, "www"));
            _skinsRoot = Path.GetFullPath(Path.Combine(baseDir, "skins"));
            Port = port;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "HttpAccept" }.Start();
        }

        public IEnumerable<string> Skins =>
            Directory.Exists(_skinsRoot)
                ? Directory.GetDirectories(_skinsRoot).Where(d => File.Exists(Path.Combine(d, "index.html"))).Select(Path.GetFileName).OrderBy(n => n)
                : Enumerable.Empty<string>();

        /// <summary>A skin's design size from &lt;meta name="h2o-size" content="WxH"&gt;, e.g. "960x440"; null if it has none.</summary>
        public string SkinSize(string skin)
        {
            try
            {
                var html = File.ReadAllText(Path.Combine(_skinsRoot, skin, "index.html"));
                var m = Regex.Match(html, @"<meta\s+name=""h2o-size""\s+content=""(\d+)x(\d+)""", RegexOptions.IgnoreCase);
                return m.Success ? $"{m.Groups[1].Value}x{m.Groups[2].Value}" : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string SkinUrl(string skin, string background) =>
            $"http://127.0.0.1:{Port}/skins/{Uri.EscapeDataString(skin)}/?bg={background}";

        void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (_stop)
                        return;
                    continue;
                }
                new Thread(() => Handle(client)) { IsBackground = true, Name = "HttpClient" }.Start();
            }
        }

        void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    string path = ReadRequestPath(stream);
                    if (path == null)
                        return;

                    if (path == "/data")
                        StreamEvents(stream);
                    else if (path == "/data.json")
                        Send(stream, 200, "application/json", Encoding.UTF8.GetBytes(_poller.Json));
                    else if (path == "/")
                        Send(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(IndexPage()));
                    else if (path == "/h2o.js" || path == "/h2o.css")
                        SendFile(stream, _wwwRoot, path.Substring(1));
                    else if (path.StartsWith("/skins/"))
                        SendFile(stream, _skinsRoot, path.Substring("/skins/".Length));
                    else
                        Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
                }
                catch (Exception)
                {
                    // client went away
                }
            }
        }

        static string ReadRequestPath(NetworkStream stream)
        {
            // Read until the end of the headers; only the request line matters
            var header = new StringBuilder();
            var buffer = new byte[1];
            while (header.Length < 8192)
            {
                if (stream.Read(buffer, 0, 1) <= 0)
                    return null;
                header.Append((char)buffer[0]);
                if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n")
                    break;
            }
            var requestLine = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            if (requestLine.Length < 2 || requestLine[0] != "GET")
                return null;
            string path = requestLine[1];
            int query = path.IndexOf('?');
            return Uri.UnescapeDataString(query >= 0 ? path.Substring(0, query) : path);
        }

        void StreamEvents(NetworkStream stream)
        {
            var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n" +
                       "Access-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\nretry: 1000\n\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);

            long sentVersion = -1;
            var lastWrite = DateTime.UtcNow;
            while (!_stop)
            {
                long version = Interlocked.Read(ref _poller.Version);
                if (version != sentVersion)
                {
                    sentVersion = version;
                    bytes = Encoding.UTF8.GetBytes("data: " + _poller.Json + "\n\n");
                    stream.Write(bytes, 0, bytes.Length);
                    lastWrite = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastWrite > TimeSpan.FromSeconds(2))
                {
                    bytes = Encoding.ASCII.GetBytes(": ping\n\n"); // also detects closed connections
                    stream.Write(bytes, 0, bytes.Length);
                    lastWrite = DateTime.UtcNow;
                }
                Thread.Sleep(2);
            }
        }

        static readonly Dictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" }, { ".css", "text/css; charset=utf-8" },
            { ".json", "application/json" }, { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".svg", "image/svg+xml" },
            { ".woff", "font/woff" }, { ".woff2", "font/woff2" }, { ".ttf", "font/ttf" }, { ".gif", "image/gif" }, { ".webp", "image/webp" },
        };

        static void SendFile(NetworkStream stream, string root, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (Directory.Exists(full))
                full = Path.Combine(full, "index.html");
            // Never serve anything outside the root
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
                return;
            }
            ContentTypes.TryGetValue(Path.GetExtension(full), out var type);
            Send(stream, 200, type ?? "application/octet-stream", File.ReadAllBytes(full));
        }

        static void Send(NetworkStream stream, int status, string contentType, byte[] body)
        {
            string reason = status == 200 ? "OK" : "Not Found";
            var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                       "Cache-Control: no-cache\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);
            stream.Write(body, 0, body.Length);
        }

        string IndexPage()
        {
            var html = new StringBuilder("<!doctype html><meta charset=utf-8><title>H2Overdrive dashboard</title>" +
                "<style>body{font:15px system-ui;background:#111;color:#ddd;margin:2em}a{color:#6cf}code{color:#fc6}</style>" +
                "<h1>H2Overdrive dashboard</h1><p>Skins (add <code>?bg=transparent|key|panel</code>, and <code>&amp;key=00ff00</code> for the key colour):</p><ul>");
            foreach (var skin in Skins)
                html.Append($"<li><a href=\"/skins/{Uri.EscapeDataString(skin)}/?bg=panel\">{WebUtility.HtmlEncode(skin)}</a></li>");
            html.Append("</ul><p>Live data: <a href=\"/data.json\">/data.json</a>, stream: <code>/data</code> (Server-Sent Events)</p>");
            return html.ToString();
        }

        public void Dispose()
        {
            _stop = true;
            _listener.Stop();
        }
    }
}
