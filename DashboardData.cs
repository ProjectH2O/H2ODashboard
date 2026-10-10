using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace H2ODashboard
{
    /// <summary>One frame of dashboard state, as published by our OpenParrot build.</summary>
    public sealed class DashboardSnapshot
    {
        public uint Frame;
        public double Time;
        public bool InRace;
        public bool Metric;
        public float Speed;            // true speed, world units/s
        public float[] Velocity = new float[3];
        public float Boost;            // 0..12
        public float Needle;           // HUD needle position 0..1
        public byte[] Dash = new byte[80]; // cabinet dashboard brightness 0..10
        public int BoostLevel;         // 0 = empty, 1 = boost fuel, 2 = gold boost
        public float GoldBoost;        // gold boost seconds left
        public int Boosting;           // 0x8000 = boost, 0x10000 = super boost
        public float RaceTime;         // player's race clock, seconds
        public float TimeLeft;         // countdown, seconds
        public int Checkpoints;        // checkpoints passed this race
        public float[] Splits = new float[10];     // race time at each checkpoint passed
        public float[] BestSplits = new float[10]; // the game's best split per checkpoint for this track
        public int Track;              // record slot 0..19 (track 0..9, +10 = mirrored)
        public bool Finished;          // player crossed the finish line
        public int Place;
        public float RecordTime;       // track record in seconds, 0 = none
        public float Pedal;            // throttle input as read
        public float EngineThrottle;   // smoothed throttle (what the speed-gauge LED ring shows)
        public float[] RawSplits = new float[10];  // race clock at each checkpoint, unrounded (0 = not captured)
        public bool PropInWater;       // propeller under water (the engine only pushes then)
        public float SpeedCap;         // current max speed in units/s, boosted while boosting
        public float HSpeed;           // horizontal speed in units/s

        /// <summary>
        /// Boost firing in the air with nothing left to give: the game adds the boost to the horizontal
        /// velocity and clamps it to max(speed, cap) (FUN_004bb560), so at that limit it adds nothing.
        /// On the water the boost is what holds the higher cap, so it is never wasted there.
        /// </summary>
        public bool BoostWasted(float tolerance) =>
            InRace && Boosting != 0 && !PropInWater && HSpeed >= Math.Max(Speed, SpeedCap) * (1 - tolerance);

        /// <summary>The throttle the game applies this frame: min(1, pedal * 1.1), not smoothed.</summary>
        public float Throttle => InRace ? Math.Max(0, Math.Min(1, Pedal * 1.1f)) : 0;
        /// <summary>Checkpoints on this track, learned from a finished race (CourseStore); 0 = not known yet.</summary>
        public int CourseCheckpoints;
        /// <summary>Time attack state from TimeAttackStore (JSON object), added to race as "ta".</summary>
        public string TimeAttackJson = "null";

        /// <summary>The game's best-split table holds this (or more) where there is no record yet.</summary>
        const float NoBest = 3600;

        // The game's speed scale (see CLAUDE.md "Needle model"): the HUD needle input is
        // v / 838.5394, the cabinet digits read the needle angle. Same number in both
        // unit modes; Metric only changes the label.
        const double NeedleSpeed = 838.5394;
        const double AngleAtZero = 0.191986, AngleAt160 = -2.92343, AngleAtClamp = -3.92699;

        /// <summary>Cabinet digit reading without smoothing, vibration or the needle cap.</summary>
        public double GameSpeed
        {
            get
            {
                if (!InRace)
                    return 0;
                double angle = AngleAtZero + Speed / NeedleSpeed * (AngleAtClamp - AngleAtZero);
                return Math.Max(0, angle / -3.44) * 180 + 5;
            }
        }

        /// <summary>HUD dial reading without smoothing or the cap.</summary>
        public double DialSpeed => InRace ? Speed / NeedleSpeed * (AngleAtClamp - AngleAtZero) / (AngleAt160 - AngleAtZero) * 160 : 0;

        /// <summary>LEDs lit on the ring around the speed gauge; the game drives it with the throttle, not the speed.</summary>
        public int SpeedBar => CountLit(32);
        public int BoostBar => CountLit(56);

        int CountLit(int start)
        {
            int lit = 0;
            for (int i = start; i < start + 24; i++)
                if (Dash[i] != 0)
                    lit++;
            return lit;
        }

        /// <summary>The 4 digits as text (best effort; skins should draw the raw segments).</summary>
        public string DigitText
        {
            get
            {
                var text = new StringBuilder();
                for (int d = 0; d < 4; d++)
                {
                    int bits = 0;
                    for (int s = 0; s < 7; s++)
                        if (Dash[d * 8 + s] != 0)
                            bits |= 1 << s;
                    text.Append(SegmentChars.TryGetChar(bits));
                    if (Dash[d * 8 + 7] != 0)
                        text.Append('.');
                }
                return text.ToString();
            }
        }

        // ---- Times as the game shows them ----------------------------------------------
        // The game always truncates to hundredths: the HUD (FUN_00500c70) takes the whole
        // seconds with _ftol2 (cvttsd2si) and then the hundredths of the remainder the same way,
        // and the results screen rounds with the FPU in chop mode. Converting here, from the
        // float itself, keeps the dashboard identical (rounding gave e.g. 130.72 for 130.7196).

        static int Centis(float t)
        {
            double d = Math.Abs((double)t);
            int whole = (int)d;
            int centis = whole * 100 + (int)((d - whole) * 100.0);
            return t < 0 ? -centis : centis;
        }

        static string Seconds(int centis) => (centis / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>The record is stored in ms (truncated hundredths x 10), so this is exact.</summary>
        int RecordCentis => (int)Math.Round(RecordTime * 100.0);

        /// <summary>
        /// The HUD's AHEAD/BEHIND number for a difference of `diff` hundredths: the game stores
        /// (diff + 0.1) / 100 and truncates its absolute value, so AHEAD shows one hundredth less
        /// than the real gap (0.52 ahead reads 0.51). Negative = ahead.
        /// </summary>
        static int HudDelta(int diff) => diff >= 0 ? diff : -(-diff - 1);

        public string ToJson(bool connected)
        {
            var c = CultureInfo.InvariantCulture;
            var j = new StringBuilder(1024);
            j.Append("{\"connected\":").Append(connected ? "true" : "false");
            j.Append(",\"inRace\":").Append(InRace ? "true" : "false");
            j.Append(",\"frame\":").Append(Frame.ToString(c));
            j.Append(",\"time\":").Append(Time.ToString("0.###", c));
            j.Append(",\"metric\":").Append(Metric ? "true" : "false");
            j.Append(",\"unit\":\"").Append(Metric ? "KPH" : "MPH").Append('"');
            j.Append(",\"speed\":{\"raw\":").Append(Speed.ToString("0.###", c));
            j.Append(",\"game\":").Append(GameSpeed.ToString("0.###", c));
            j.Append(",\"dial\":").Append(DialSpeed.ToString("0.###", c)).Append('}');
            j.Append(",\"velocity\":[").Append(Velocity[0].ToString("0.###", c)).Append(',')
             .Append(Velocity[1].ToString("0.###", c)).Append(',').Append(Velocity[2].ToString("0.###", c)).Append(']');
            j.Append(",\"boost\":").Append(Boost.ToString("0.###", c));
            j.Append(",\"needle\":").Append(Needle.ToString("0.####", c));
            j.Append(",\"digits\":{\"text\":\"").Append(DigitText).Append("\",\"segments\":");
            AppendBytes(j, 0, 32);
            j.Append("},\"meters\":{\"speed\":");
            AppendBytes(j, 32, 24);
            j.Append(",\"boost\":");
            AppendBytes(j, 56, 24);
            j.Append("},\"speedBar\":").Append(SpeedBar.ToString(c));
            j.Append(",\"boostBar\":").Append(BoostBar.ToString(c));
            j.Append(",\"throttle\":").Append(Throttle.ToString("0.###", c));
            j.Append(",\"pedal\":").Append(Pedal.ToString("0.###", c));
            j.Append(",\"engineThrottle\":").Append(EngineThrottle.ToString("0.###", c));
            j.Append(",\"boostLevel\":").Append(BoostLevel.ToString(c));
            j.Append(",\"gold\":").Append(BoostLevel == 2 || GoldBoost > 0 ? "true" : "false");
            j.Append(",\"goldTime\":").Append(GoldBoost.ToString("0.###", c));
            j.Append(",\"boosting\":").Append(Boosting != 0 ? "true" : "false");
            j.Append(",\"airborne\":").Append(InRace && !PropInWater ? "true" : "false");
            j.Append(",\"speedCap\":").Append(SpeedCap.ToString("0.###", c));
            j.Append(",\"hSpeed\":").Append(HSpeed.ToString("0.###", c));
            j.Append(",\"boostWasted\":").Append(BoostWasted(0.01f) ? "true" : "false");
            j.Append(",\"race\":{\"track\":").Append(Track.ToString(c));
            j.Append(",\"course\":").Append(CourseCheckpoints > 0 ? CourseCheckpoints.ToString(c) : "null");
            j.Append(",\"finished\":").Append(InRace && Finished ? "true" : "false");
            j.Append(",\"place\":").Append(Place.ToString(c));
            j.Append(",\"record\":").Append(RecordTime > 0 ? Seconds(RecordCentis) : "null");
            j.Append(",\"time\":").Append(Seconds(Centis(RaceTime)));
            j.Append(",\"timeLeft\":").Append(TimeLeft.ToString("0.###", c));
            j.Append(",\"checkpoint\":").Append(Checkpoints.ToString(c));
            j.Append(",\"splits\":[");
            int passed = Math.Min(Checkpoints, Splits.Length);
            for (int i = 0; i < passed; i++)
            {
                if (i > 0)
                    j.Append(',');
                bool hasBest = BestSplits[i] > 0 && BestSplits[i] < NoBest;
                int time = Centis(Splits[i]), best = Centis(BestSplits[i]);
                j.Append("{\"time\":").Append(Seconds(time));
                j.Append(",\"best\":").Append(hasBest ? Seconds(best) : "null");
                j.Append(",\"delta\":").Append(hasBest ? Seconds(HudDelta(time - best)) : "null").Append('}');
            }
            j.Append("],\"best\":[");
            for (int i = 0; i < BestSplits.Length; i++)
            {
                if (i > 0)
                    j.Append(',');
                j.Append(BestSplits[i] > 0 && BestSplits[i] < NoBest ? Seconds(Centis(BestSplits[i])) : "null");
            }
            j.Append(']');
            j.Append(",\"ta\":").Append(TimeAttackJson);
            // The finish line isn't a checkpoint: its "split" is the final race time against the track record
            if (InRace && Finished)
            {
                int final = Centis(RaceTime);
                j.Append(",\"finish\":{\"time\":").Append(Seconds(final));
                j.Append(",\"best\":").Append(RecordTime > 0 ? Seconds(RecordCentis) : "null");
                j.Append(",\"delta\":").Append(RecordTime > 0 ? Seconds(final - RecordCentis) : "null").Append('}');
            }
            else
            {
                j.Append(",\"finish\":null");
            }
            j.Append("}}");
            return j.ToString();
        }

        void AppendBytes(StringBuilder j, int start, int count)
        {
            j.Append('[');
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    j.Append(',');
                j.Append(Dash[start + i]);
            }
            j.Append(']');
        }
    }

    /// <summary>Inverse of the game's character -> 7-segment table (FUN_004e0d60).</summary>
    static class SegmentChars
    {
        static readonly (int bits, char c)[] Table =
        {
            (0x00, ' '), (0x3F, '0'), (0x06, '1'), (0x5B, '2'), (0x4F, '3'), (0x66, '4'), (0x6D, '5'), (0x7D, '6'),
            (0x07, '7'), (0x7F, '8'), (0x6F, '9'), (0x40, '-'), (0x48, '='), (0x77, 'A'), (0x39, 'C'), (0x5E, 'd'),
            (0x79, 'E'), (0x71, 'F'), (0x76, 'H'), (0x0E, 'J'), (0x38, 'L'), (0x54, 'n'), (0x73, 'P'), (0x67, 'q'),
            (0x50, 'r'), (0x78, 't'), (0x3E, 'U'), (0x6E, 'y'), (0x7C, 'b'), (0x58, 'c'), (0x74, 'h'), (0x5C, 'o'),
            (0x1C, 'u'), (0x08, '_'),
        };

        public static char TryGetChar(int bits)
        {
            foreach (var (b, c) in Table)
                if (b == bits)
                    return c;
            return '?';
        }
    }

    /// <summary>Reads Local\H2ODashboard (see PublishDashboard in OpenParrot's H2Overdrive.cpp).</summary>
    public sealed class DashboardFeed : IDisposable
    {
        const string MappingName = @"Local\H2ODashboard";
        const uint Magic = 0x48324F44; // MSVC 'H2OD'
        const int Size = 320;

        MemoryMappedFile _file;
        MemoryMappedViewAccessor _view;
        readonly byte[] _buffer = new byte[Size];
        uint _lastFrame;
        DateTime _lastFrameChange = DateTime.MinValue;

        /// <summary>True while the game is publishing new frames.</summary>
        public bool Connected => _view != null && DateTime.UtcNow - _lastFrameChange < TimeSpan.FromSeconds(1);

        public DashboardSnapshot Read()
        {
            if (_view == null && !TryOpen())
                return null;

            for (int attempt = 0; attempt < 100; attempt++)
            {
                int before = _view.ReadInt32(12);
                if ((before & 1) != 0)
                {
                    Thread.SpinWait(50);
                    continue;
                }
                _view.ReadArray(0, _buffer, 0, Size);
                Thread.MemoryBarrier();
                if (_view.ReadInt32(12) != before)
                    continue;
                return Parse();
            }
            return null;
        }

        bool TryOpen()
        {
            try
            {
                _file = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.Read);
                _view = _file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.Read);
                if (_view.ReadUInt32(0) != Magic || _view.ReadUInt32(4) != 6 || _view.ReadUInt32(8) != Size)
                {
                    Dispose();
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                Dispose();
                return false;
            }
        }

        DashboardSnapshot Parse()
        {
            var b = _buffer;
            var s = new DashboardSnapshot
            {
                Time = BitConverter.ToDouble(b, 16),
                Frame = BitConverter.ToUInt32(b, 24),
                InRace = BitConverter.ToInt32(b, 28) != 0,
                Metric = BitConverter.ToInt32(b, 32) != 0,
                Speed = BitConverter.ToSingle(b, 36),
                Boost = BitConverter.ToSingle(b, 52),
                Needle = BitConverter.ToSingle(b, 56),
            };
            s.Velocity[0] = BitConverter.ToSingle(b, 40);
            s.Velocity[1] = BitConverter.ToSingle(b, 44);
            s.Velocity[2] = BitConverter.ToSingle(b, 48);
            Buffer.BlockCopy(b, 60, s.Dash, 0, 80);
            s.BoostLevel = BitConverter.ToInt32(b, 140);
            s.GoldBoost = BitConverter.ToSingle(b, 144);
            s.Boosting = BitConverter.ToInt32(b, 148);
            s.RaceTime = BitConverter.ToSingle(b, 152);
            s.TimeLeft = BitConverter.ToSingle(b, 156);
            s.Checkpoints = BitConverter.ToInt32(b, 160);
            Buffer.BlockCopy(b, 164, s.Splits, 0, 40);
            Buffer.BlockCopy(b, 204, s.BestSplits, 0, 40);
            s.Track = BitConverter.ToInt32(b, 244);
            s.Finished = BitConverter.ToInt32(b, 248) != 0;
            s.Place = BitConverter.ToInt32(b, 252);
            s.RecordTime = BitConverter.ToSingle(b, 256);
            s.Pedal = BitConverter.ToSingle(b, 260);
            s.EngineThrottle = BitConverter.ToSingle(b, 264);
            Buffer.BlockCopy(b, 268, s.RawSplits, 0, 40);
            s.PropInWater = BitConverter.ToSingle(b, 308) > 0;
            s.SpeedCap = BitConverter.ToSingle(b, 312);
            s.HSpeed = BitConverter.ToSingle(b, 316);

            if (s.Frame != _lastFrame)
            {
                _lastFrame = s.Frame;
                _lastFrameChange = DateTime.UtcNow;
            }
            return s;
        }

        public void Dispose()
        {
            _view?.Dispose();
            _file?.Dispose();
            _view = null;
            _file = null;
        }
    }

    /// <summary>
    /// Number of checkpoints per track, learned when a race is finished (the game has no count we
    /// could read; its best-split table always has 10 slots, the unused ones filled with old values).
    /// Saved in H2ODashboard.courses.ini next to the exe as "track=count".
    /// </summary>
    public sealed class CourseStore
    {
        readonly string _path;
        readonly Dictionary<int, int> _counts = new Dictionary<int, int>();

        public CourseStore(string path)
        {
            _path = path;
            try
            {
                if (!File.Exists(path))
                    return;
                foreach (var line in File.ReadAllLines(path))
                {
                    var parts = line.Split('=');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var track) && int.TryParse(parts[1], out var count) && count > 0)
                        _counts[track] = count;
                }
            }
            catch (IOException)
            {
            }
        }

        public int Get(int track) => _counts.TryGetValue(track, out var count) ? count : 0;

        /// <summary>Call every frame; learns the count at the finish line.</summary>
        public void Observe(DashboardSnapshot s)
        {
            if (!s.InRace || !s.Finished || s.Checkpoints <= 0 || s.Checkpoints > 10)
                return;
            // Keep the largest seen, in case a checkpoint was missed (e.g. a shortcut)
            if (Get(s.Track) >= s.Checkpoints)
                return;
            _counts[s.Track] = s.Checkpoints;
            try
            {
                var text = new StringBuilder();
                foreach (var kv in _counts)
                    text.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append('=')
                        .Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(_path, text.ToString());
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
