using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace H2ODashboard
{
    /// <summary>A complete run: final time and the race time at each checkpoint, in milliseconds.</summary>
    public sealed class RunTimes
    {
        public int Final;
        public int[] Splits = new int[0];

        public void AppendJson(StringBuilder j)
        {
            var c = CultureInfo.InvariantCulture;
            j.Append("{\"time\":").Append(Final.ToString(c)).Append(",\"splits\":[");
            j.Append(string.Join(",", Splits.Select(s => s.ToString(c)))).Append("]}");
        }
    }

    /// <summary>
    /// Time attack timing kept outside the game, at millisecond precision: the run in progress
    /// (unrounded race clock at each checkpoint, from the OpenParrot split hook), the personal best
    /// per track (the fastest complete run, with its splits; the game only keeps a best per
    /// checkpoint, from different runs) and world records typed in by hand.
    ///
    /// Files next to the exe, same format (track = final ; split 1 ; split 2 ...):
    ///   H2ODashboard.pb.ini  personal bests, written by the dashboard
    ///   H2ODashboard.wr.ini  world records, edited by hand (created with instructions if missing)
    /// Both are reloaded when they change on disk.
    /// </summary>
    public sealed class TimeAttackStore
    {
        readonly RecordFile _pb, _wr;

        // Run in progress (cleared when the player leaves the race)
        bool _hasRun, _live, _saved, _newPb;
        int _track, _time;
        int[] _splits = new int[0];
        int? _finish;
        RunTimes _pbAtStart; // what this run is compared with: the PB before it started
        bool _wasInRace;
        float _lastRaceTime;
        int _lastCheckpoints;
        DateTime _lastReloadCheck;

        public TimeAttackStore(string directory)
        {
            _pb = new RecordFile(Path.Combine(directory, "H2ODashboard.pb.ini"), PbHeader);
            _wr = new RecordFile(Path.Combine(directory, "H2ODashboard.wr.ini"), WrHeader);
            _wr.CreateIfMissing();
        }

        /// <summary>Call for every snapshot (after CourseStore has filled CourseCheckpoints); sets s.TimeAttackJson.</summary>
        public void Observe(DashboardSnapshot s)
        {
            if (DateTime.UtcNow - _lastReloadCheck > TimeSpan.FromSeconds(1))
            {
                _lastReloadCheck = DateTime.UtcNow;
                _pb.ReloadIfChanged();
                _wr.ReloadIfChanged();
            }

            if (s.InRace)
            {
                bool restarted = _wasInRace && (s.RaceTime + 0.25f < _lastRaceTime || s.Checkpoints < _lastCheckpoints);
                if (!_wasInRace || restarted || !_hasRun)
                    StartRun(s.Track);
                else if (s.Track != _track && s.Checkpoints == 0)
                    StartRun(s.Track); // track set late; nothing timed yet
                Update(s);
            }
            else if (_hasRun)
            {
                // Left the race: clear the run so the skin doesn't keep showing it
                StartRun(_track);
                _hasRun = false;
                _live = false;
            }
            _wasInRace = s.InRace;
            _lastRaceTime = s.RaceTime;
            _lastCheckpoints = s.Checkpoints;

            s.TimeAttackJson = ToJson(s);
        }

        void StartRun(int track)
        {
            _hasRun = true;
            _track = track;
            _saved = false;
            _newPb = false;
            _finish = null;
            _splits = new int[0];
            _time = 0;
            _pbAtStart = _pb.Get(track);
        }

        // Truncated like the game's own displays, so its hundredths are always ours with the last digit dropped.
        static int Ms(float seconds) => (int)Math.Floor(seconds * 1000.0);

        void Update(DashboardSnapshot s)
        {
            _live = true;
            _time = Ms(s.RaceTime);
            int passed = Math.Min(s.Checkpoints, 10);
            if (_splits.Length != passed)
            {
                // The raw clock from the split hook; the game's rounded value if the hook isn't there
                _splits = Enumerable.Range(0, passed)
                    .Select(i => Ms(s.RawSplits[i] > 0 ? s.RawSplits[i] : s.Splits[i]))
                    .ToArray();
            }

            if (s.Finished && _finish == null)
            {
                _finish = _time;
                // A complete run: every checkpoint of the course passed (CourseStore keeps the most seen)
                if (!_saved && passed > 0 && passed >= s.CourseCheckpoints && _splits.All(t => t > 0))
                {
                    _saved = true;
                    var current = _pb.Get(_track);
                    if (current == null || _time < current.Final)
                    {
                        _newPb = true;
                        _pb.Set(_track, new RunTimes { Final = _time, Splits = _splits.ToArray() });
                    }
                }
            }
        }

        string ToJson(DashboardSnapshot s)
        {
            var c = CultureInfo.InvariantCulture;
            int track = _hasRun ? _track : s.Track;
            var j = new StringBuilder(256);
            j.Append("{\"live\":").Append(_live ? "true" : "false");
            j.Append(",\"track\":").Append(track.ToString(c));
            j.Append(",\"time\":").Append(_hasRun ? _time.ToString(c) : "null");
            j.Append(",\"splits\":[").Append(string.Join(",", _splits.Select(t => t.ToString(c)))).Append(']');
            j.Append(",\"finish\":").Append(_finish?.ToString(c) ?? "null");
            j.Append(",\"newPb\":").Append(_newPb ? "true" : "false");
            j.Append(",\"pb\":");
            var pb = _hasRun ? _pbAtStart : _pb.Get(track);
            if (pb != null) pb.AppendJson(j); else j.Append("null");
            j.Append(",\"wr\":");
            var wr = _wr.Get(track);
            if (wr != null) wr.AppendJson(j); else j.Append("null");
            j.Append('}');
            return j.ToString();
        }

        const string FormatHelp =
            "# One line per track:  track = final time ; time at checkpoint 1 ; time at checkpoint 2 ; ...\n" +
            "#   track: the record slot, 0-9, +10 for the mirrored track (the timeattack skin shows it\n" +
            "#          as \"TRACK n\" and H2ODashboard.pb.ini lists the tracks you have finished)\n" +
            "#   times: 2'40\"038 or 2:40.038 or 160.038 (seconds). Checkpoint times are the race time\n" +
            "#          when the checkpoint is passed (not section times); they are optional.\n";

        const string PbHeader =
            "# H2Overdrive dashboard: personal bests (fastest complete run per track), written by the dashboard.\n" +
            FormatHelp;

        const string WrHeader =
            "# H2Overdrive dashboard: world records, compared with in the timeattack skin. Edit by hand;\n" +
            "# the dashboard reloads this file when it changes. Lines starting with # are ignored.\n" +
            FormatHelp +
            "# Example (track 0 in 2'40\"038 with 4 checkpoints):\n" +
            "# 0 = 2'40\"038 ; 0'39\"912 ; 1'19\"500 ; 1'58\"870 ; 2'20\"101\n";

        /// <summary>A "track = final ; splits" file; times are kept in milliseconds.</summary>
        sealed class RecordFile
        {
            readonly string _path, _header;
            readonly Dictionary<int, RunTimes> _runs = new Dictionary<int, RunTimes>();
            DateTime _loadedWriteTime = DateTime.MinValue;

            public RecordFile(string path, string header)
            {
                _path = path;
                _header = header;
                ReloadIfChanged();
            }

            public RunTimes Get(int track) => _runs.TryGetValue(track, out var run) ? run : null;

            public void CreateIfMissing()
            {
                if (!File.Exists(_path))
                    Save();
            }

            public void Set(int track, RunTimes run)
            {
                _runs[track] = run;
                Save();
            }

            public void ReloadIfChanged()
            {
                try
                {
                    var time = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
                    if (time == _loadedWriteTime)
                        return;
                    _loadedWriteTime = time;
                    _runs.Clear();
                    if (time == DateTime.MinValue)
                        return;
                    foreach (var raw in File.ReadAllLines(_path))
                    {
                        var line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (line.StartsWith("#") || eq < 0 || !int.TryParse(line.Substring(0, eq).Trim(), out var track))
                            continue;
                        var times = line.Substring(eq + 1).Split(';').Select(t => ParseTime(t.Trim())).ToArray();
                        if (times.Length == 0 || times[0] == null)
                            continue;
                        // Splits up to the first blank or unreadable one
                        var splits = times.Skip(1).TakeWhile(t => t != null).Select(t => t.Value).ToArray();
                        _runs[track] = new RunTimes { Final = times[0].Value, Splits = splits };
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            void Save()
            {
                try
                {
                    var text = new StringBuilder(_header);
                    foreach (var kv in _runs.OrderBy(kv => kv.Key))
                    {
                        text.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append(" = ").Append(FormatTime(kv.Value.Final));
                        foreach (var split in kv.Value.Splits)
                            text.Append(" ; ").Append(FormatTime(split));
                        text.Append('\n');
                    }
                    File.WriteAllText(_path, text.ToString());
                    _loadedWriteTime = File.GetLastWriteTimeUtc(_path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>m'ss"mmm, the Initial D style the skin shows.</summary>
        public static string FormatTime(int ms)
        {
            int m = ms / 60000, s = ms / 1000 % 60, f = ms % 1000;
            return $"{m}'{s:00}\"{f:000}";
        }

        static readonly Regex TimePattern = new Regex(@"^(?:(\d+)\s*['’:]\s*)?(\d+(?:\.\d*)?)(?:\s*[""”.]\s*(\d{1,3}))?$");

        /// <summary>2'40"038, 2:40.038, 160.038 or 160 -> milliseconds; null if not a time.</summary>
        public static int? ParseTime(string text)
        {
            var m = TimePattern.Match(text);
            if (!m.Success)
                return null;
            double seconds = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[3].Success)
            {
                if (m.Groups[2].Value.Contains('.'))
                    return null; // 40.5"123
                seconds += int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / Math.Pow(10, m.Groups[3].Value.Length);
            }
            if (m.Groups[1].Success)
                seconds += 60 * int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return (int)Math.Round(seconds * 1000);
        }
    }
}
