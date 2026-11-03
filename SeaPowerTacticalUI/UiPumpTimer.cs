using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-124 (R1): per-module time of the UiCore frame loop. Pure: the clock comes in; the hot path (Start / Stop* / BeginFrame) allocates nothing.
    /// A module's frame time is the sum of its Update and every OnGUI call of that frame; the pseudo-module "mission" is TickMission.
    /// </summary>
    internal sealed class UiPumpTimer
    {
        public const int TopN = 5;
        public const double ShowMinMs = 0.01;            // modules below this average are left out of "top="
        public const string MissionId = "mission";

        private readonly Func<long> _ticks;
        private readonly long _perSecond;
        private string[] _ids = new string[0];           // owners in registration order, then "mission"
        private long[] _cur = new long[1];               // ticks of the frame being built, per slot
        private long[] _sum = new long[1];               // window sums of closed frames
        private long[] _max = new long[1];               // window max of one frame
        private long _totalSum, _totalMax;
        private int _frames;
        private bool _dirty;                             // something was timed since the last BeginFrame

        /// <param name="ticks">Stopwatch.GetTimestamp in the game; a counter in tests.</param>
        /// <param name="ticksPerSecond">Stopwatch.Frequency in the game.</param>
        public UiPumpTimer(Func<long> ticks, long ticksPerSecond)
        {
            _ticks = ticks; _perSecond = ticksPerSecond <= 0 ? 1 : ticksPerSecond;
        }

        /// <summary>[Ui] PumpTiming; false = Start() returns 0 and Stop* do nothing.</summary>
        public bool Enabled { get; set; }

        /// <summary>UiCore: module ids in registration order (as UiGuard). Resets the window.</summary>
        public void SetOwners(string[] ids)
        {
            int n = ids == null ? 0 : ids.Length;
            _ids = new string[n + 1];
            for (int i = 0; i < n; i++) _ids[i] = ids[i];
            _ids[n] = MissionId;
            _cur = new long[n + 1]; _sum = new long[n + 1]; _max = new long[n + 1];
            ResetWindow();
        }

        private void ResetWindow()
        {
            for (int i = 0; i < _sum.Length; i++) { _sum[i] = 0; _max[i] = 0; _cur[i] = 0; }
            _totalSum = 0; _totalMax = 0; _frames = 0; _dirty = false;
        }

        /// <summary>0 when disabled.</summary>
        public long Start()
        {
            if (!Enabled) return 0;
            long t = _ticks();
            return t == 0 ? 1 : t;
        }

        public void StopUpdate(int owner, long t0) { Add(owner, t0); }
        public void StopGui(int owner, long t0) { Add(owner, t0); }
        public void StopMission(long t0) { Add(_ids.Length - 1, t0); }

        private void Add(int slot, long t0)
        {
            if (t0 == 0 || slot < 0 || slot >= _cur.Length) return;
            long d = _ticks() - t0;
            if (d > 0) _cur[slot] += d;
            _dirty = true;
        }

        /// <summary>UiCore.Update top: closes the previous frame (its Update + every OnGUI).</summary>
        public void BeginFrame()
        {
            if (!_dirty) return;
            long total = 0;
            for (int i = 0; i < _cur.Length; i++)
            {
                long c = _cur[i];
                _sum[i] += c;
                if (c > _max[i]) _max[i] = c;
                total += c;
                _cur[i] = 0;
            }
            _totalSum += total;
            if (total > _totalMax) _totalMax = total;
            _frames++;
            _dirty = false;
        }

        private double Ms(long ticks) { return ticks * 1000.0 / _perSecond; }

        /// <summary>The same window as numbers: id -> { avgMs, maxMs, frames }, then "total". Does not reset.</summary>
        public IList<KeyValuePair<string, double[]>> Snapshot()
        {
            var list = new List<KeyValuePair<string, double[]>>();
            for (int i = 0; i < _ids.Length; i++)
                list.Add(new KeyValuePair<string, double[]>(_ids[i], new[] { _frames == 0 ? 0.0 : Ms(_sum[i]) / _frames, Ms(_max[i]), _frames }));
            list.Add(new KeyValuePair<string, double[]>("total", new[] { _frames == 0 ? 0.0 : Ms(_totalSum) / _frames, Ms(_totalMax), _frames }));
            return list;
        }

        /// <summary>"1.31/27.60 top=radar:0.42/3.10,overview:0.30/1.20" (avg ms per frame / max ms of one frame; invariant culture);
        /// "off" when disabled, "0.00/0.00" without frames. Resets the window.</summary>
        public string HeartbeatText()
        {
            if (!Enabled) return "off";
            var inv = CultureInfo.InvariantCulture;
            if (_frames == 0) return "0.00/0.00";
            var sb = new StringBuilder();
            sb.Append((Ms(_totalSum) / _frames).ToString("F2", inv)).Append('/').Append(Ms(_totalMax).ToString("F2", inv));
            var picked = new bool[_ids.Length];
            bool first = true;
            for (int k = 0; k < TopN; k++)
            {
                int best = -1;
                for (int i = 0; i < _ids.Length; i++)
                    if (!picked[i] && _sum[i] > 0 && (best < 0 || _sum[i] > _sum[best])) best = i;
                if (best < 0 || Ms(_sum[best]) / _frames < ShowMinMs) break;
                picked[best] = true;
                sb.Append(first ? " top=" : ",").Append(_ids[best]).Append(':')
                  .Append((Ms(_sum[best]) / _frames).ToString("F2", inv)).Append('/').Append(Ms(_max[best]).ToString("F2", inv));
                first = false;
            }
            ResetWindow();
            return sb.ToString();
        }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            try
            {
                long t = 100;
                var tm = new UiPumpTimer(() => t, 1000);
                tm.SetOwners(new[] { "a", "b", "c" });
                if (tm.Start() != 0 || tm.HeartbeatText() != "off") return "timer: disabled must be off";
                tm.Enabled = true;
                if (tm.HeartbeatText() != "0.00/0.00") return "timer: no frames = " + tm.HeartbeatText();
                // frame 1: a = 2 ms, mission 1 ms
                tm.BeginFrame();
                long m0 = tm.Start(); t += 1; tm.StopMission(m0);
                long a0 = tm.Start(); t += 2; tm.StopUpdate(0, a0);
                // frame 2: a = Update 1 + two OnGUI 1 + 1 = 3 ms; b = 0.005 ms-class (below threshold)
                tm.BeginFrame();
                a0 = tm.Start(); t += 1; tm.StopUpdate(0, a0);
                a0 = tm.Start(); t += 1; tm.StopGui(0, a0);
                a0 = tm.Start(); t += 1; tm.StopGui(0, a0);
                tm.BeginFrame();   // closes frame 2
                var snap = tm.Snapshot();
                var sa = snap[0].Value;
                if (snap[0].Key != "a" || Math.Abs(sa[0] - 2.5) > 1e-9 || Math.Abs(sa[1] - 3.0) > 1e-9 || sa[2] != 2) return "timer: a snapshot = " + sa[0] + "/" + sa[1] + "/" + sa[2];
                var tot = snap[snap.Count - 1].Value;
                if (snap[snap.Count - 1].Key != "total" || Math.Abs(tot[0] - 3.0) > 1e-9 || Math.Abs(tot[1] - 3.0) > 1e-9) return "timer: total = " + tot[0] + "/" + tot[1];
                string h = tm.HeartbeatText();
                if (h != "3.00/3.00 top=a:2.50/3.00,mission:0.50/1.00") return "timer: text = " + h;
                if (tm.HeartbeatText() != "0.00/0.00") return "timer: window must reset";
                // top order, TopN, threshold
                tm.SetOwners(new[] { "m1", "m2", "m3", "m4", "m5", "m6", "tiny" });
                tm.BeginFrame();
                for (int i = 0; i < 6; i++) { long s = tm.Start(); t += 1 + i; tm.StopUpdate(i, s); }
                long ti = tm.Start(); t += 0; tm.StopUpdate(6, ti);   // zero ticks
                tm.BeginFrame();
                h = tm.HeartbeatText();
                if (h != "21.00/21.00 top=m6:6.00/6.00,m5:5.00/5.00,m4:4.00/4.00,m3:3.00/3.00,m2:2.00/2.00") return "timer: top = " + h;
                // culture
                var old = CultureInfo.CurrentCulture;
                try
                {
                    var ru = (CultureInfo)CultureInfo.InvariantCulture.Clone(); ru.NumberFormat.NumberDecimalSeparator = ",";   // like ru-RU, works in invariant-globalization stands
                    CultureInfo.CurrentCulture = ru;
                    tm.BeginFrame(); long s2 = tm.Start(); t += 1; tm.StopUpdate(0, s2); tm.BeginFrame();
                    h = tm.HeartbeatText();
                    if (h != "1.00/1.00 top=m1:1.00/1.00") return "timer: culture = " + h;
                }
                finally { CultureInfo.CurrentCulture = old; }
                // bad owners
                tm.StopUpdate(-1, 5); tm.StopUpdate(99, 5); tm.StopGui(99, 5); tm.StopUpdate(0, 0);
                tm.Enabled = false;
                if (tm.Start() != 0) return "timer: disabled Start";
                return null;
            }
            catch (Exception e) { return "timer: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
