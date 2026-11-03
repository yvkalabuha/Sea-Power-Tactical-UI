// T-130 (R7): one mission-change detector for the whole UI mod, and a watchdog for stuck mouse blocks. Pure (no Unity, no game types).
using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge.Ui
{
    internal sealed class MissionEnter
    {
        public string SavePath;      // the .sav the mission was loaded from (T-198: Globals.currentMissionFilePath), null = a new mission
        public string MarkerPath;    // the last_save marker on entry: the base of the marker observers (can differ from SavePath: "saved A, loaded B")
        public SavStamp Stamp;       // header of SavePath, null = unknown (not read, or an error)
        public double MissionTimeS; public int Generation;
    }

    /// <summary>
    /// Watches the mission object (ObjectsManager) through a WeakReference so it never keeps a mission alive.
    /// Entered runs the subscribers by ascending order, Left by descending order; a throwing subscriber is logged and the rest still run.
    /// Leaving to the menu is debounced (LeaveDebounceS of real time without a mission); a different object leaves at once ("switch").
    /// </summary>
    internal sealed class MissionLifecycle
    {
        public const double LeaveDebounceS = 2.0;

        private sealed class Sub
        {
            public int Order; public string Id; public Action<MissionEnter> Entered; public Action<string> Left; public int Seq;
        }

        private readonly Action<string> _log;
        private readonly List<Sub> _subs = new List<Sub>();
        private WeakReference _ref;
        private bool _in;
        private double _nullSince = -1;
        private int _seq;
        private const int PrevRing = 8;
        private readonly WeakReference[] _prev = new WeakReference[PrevRing];   // T-212: the last left missions, held weakly
        private int _prevN;

        public MissionLifecycle(Action<string> log) { _log = log; }

        public bool InMission { get { return _in; } }
        public int Generation { get; private set; }

        /// <summary>T-198: the last_save marker on entry; null = the marker is the save path (the behaviour before T-198).</summary>
        public Func<string> MarkerOf;
        /// <summary>T-198: header stamp of the loaded .sav; null = never read.</summary>
        public Func<string, SavStamp> StampOf;

        /// <summary>left may be null. Equal orders keep the subscription sequence (Left: reverse).</summary>
        public void Subscribe(int order, string id, Action<MissionEnter> entered, Action<string> left)
        {
            var s = new Sub { Order = order, Id = id, Entered = entered, Left = left, Seq = _seq++ };
            int i = _subs.Count;
            while (i > 0 && (_subs[i - 1].Order > order)) i--;
            _subs.Insert(i, s);
        }

        public void Tick(object missionOrNull, Func<string> savePath, Func<double?> missionTimeS, double realT)
        {
            if (missionOrNull == null)
            {
                if (!_in) return;
                if (_nullSince < 0) { _nullSince = realT; return; }
                if (realT - _nullSince >= LeaveDebounceS)
                {
                    _in = false; _nullSince = -1; Track(_ref); _ref = null;
                    RunLeft("menu");
                }
                return;
            }
            _nullSince = -1;
            if (_in && _ref != null && ReferenceEquals(_ref.Target, missionOrNull)) return;
            if (_in) { _in = false; Track(_ref); RunLeft("switch"); }
            _ref = new WeakReference(missionOrNull);
            _in = true;
            Generation++;
            var e = new MissionEnter { Generation = Generation };
            try { e.SavePath = savePath == null ? null : savePath(); } catch (Exception ex) { Log("save path: " + ex.Message); }
            try { var t = missionTimeS == null ? null : missionTimeS(); e.MissionTimeS = t ?? 0; } catch (Exception ex) { Log("mission time: " + ex.Message); }
            e.MarkerPath = e.SavePath;
            if (MarkerOf != null) { try { e.MarkerPath = MarkerOf(); } catch (Exception ex) { Log("marker: " + ex.Message); } }
            if (StampOf != null && e.SavePath != null) { try { e.Stamp = StampOf(e.SavePath); } catch (Exception ex) { Log("stamp: " + ex.Message); e.Stamp = null; } }
            for (int i = 0; i < _subs.Count; i++)
            {
                var s = _subs[i];
                if (s.Entered == null) continue;
                try { s.Entered(e); } catch (Exception ex) { Log("entered " + s.Id + ": " + ex.GetType().Name + ": " + ex.Message); }
            }
        }

        public int SubscriberCount { get { return _subs.Count; } }

        private void Track(WeakReference r)
        {
            if (r == null) return;
            _prev[_prevN % PrevRing] = r; _prevN++;
        }

        public int PrevTracked() { return Math.Min(_prevN, PrevRing); }

        /// <summary>T-212: how many of the last left missions are still alive (a leak when it stays above 0 after a GC). Forces no GC.</summary>
        public int PrevAlive()
        {
            int n = 0;
            for (int i = 0; i < PrevRing; i++) { var w = _prev[i]; if (w != null && w.IsAlive) n++; }
            return n;
        }

        public string CountersJson()
        {
            return "{\"generation\":" + Generation + ",\"subscribers\":" + SubscriberCount + ",\"prev_alive\":" + PrevAlive() + ",\"prev_tracked\":" + PrevTracked() + "}";
        }

        private void RunLeft(string why)
        {
            for (int i = _subs.Count - 1; i >= 0; i--)
            {
                var s = _subs[i];
                if (s.Left == null) continue;
                try { s.Left(why); } catch (Exception ex) { Log("left " + s.Id + ": " + ex.GetType().Name + ": " + ex.Message); }
            }
        }

        private void Log(string m) { if (_log != null) _log("mission lifecycle: " + m); }

        public static string SelfTest()
        {
            try
            {
                var log = new List<string>();
                var ev = new List<string>();
                var m = new MissionLifecycle(log.Add);
                m.Subscribe(30, "c", e => ev.Add("E30:" + e.Generation + ":" + e.SavePath + ":" + e.MissionTimeS), why => ev.Add("L30:" + why));
                m.Subscribe(10, "a", e => ev.Add("E10"), why => ev.Add("L10"));
                m.Subscribe(20, "b", e => ev.Add("E20"), null);
                var a = new object(); var b = new object();
                Func<string> sp = () => "/s.sav"; Func<double?> tm = () => 5.5;
                m.Tick(null, sp, tm, 0);
                if (ev.Count != 0 || m.InMission) return "null at start must do nothing";
                m.Tick(a, sp, tm, 1);
                if (string.Join(",", ev.ToArray()) != "E10,E20,E30:1:/s.sav:5.5") return "enter order: " + string.Join(",", ev.ToArray());
                if (m.Generation != 1 || !m.InMission) return "generation after enter";
                ev.Clear();
                m.Tick(a, sp, tm, 2);
                if (ev.Count != 0) return "same object must do nothing";
                m.Tick(null, sp, tm, 10); m.Tick(null, sp, tm, 11.5); m.Tick(a, sp, tm, 11.6);
                if (ev.Count != 0 || !m.InMission) return "blink of 1.5 s must do nothing: " + string.Join(",", ev.ToArray());
                m.Tick(null, sp, tm, 20); m.Tick(null, sp, tm, 21.9);
                if (ev.Count != 0) return "2 s not reached";
                m.Tick(null, sp, tm, 22.1);
                if (string.Join(",", ev.ToArray()) != "L30:menu,L10") return "leave order: " + string.Join(",", ev.ToArray());
                if (m.InMission) return "InMission after leave";
                ev.Clear();
                m.Tick(null, sp, tm, 40);
                if (ev.Count != 0) return "second null must not leave again";
                m.Tick(b, sp, tm, 41);
                if (ev.Count != 3 || m.Generation != 2 || ev[2] != "E30:2:/s.sav:5.5") return "enter B: " + string.Join(",", ev.ToArray());
                ev.Clear();
                m.Tick(a, sp, tm, 42);
                if (string.Join(",", ev.ToArray()) != "L30:switch,L10,E10,E20,E30:3:/s.sav:5.5") return "switch: " + string.Join(",", ev.ToArray());

                // a throwing subscriber does not stop the others and leaves one log line
                log.Clear(); ev.Clear();
                var m2 = new MissionLifecycle(log.Add);
                m2.Subscribe(1, "x", e => ev.Add("x"), null);
                m2.Subscribe(2, "boom", e => { throw new InvalidOperationException("boom"); }, why => { throw new InvalidOperationException("boom"); });
                m2.Subscribe(3, "y", e => ev.Add("y"), why => ev.Add("ly"));
                m2.Tick(new object(), null, null, 0);
                if (string.Join(",", ev.ToArray()) != "x,y" || log.Count != 1) return "throwing subscriber (enter): " + string.Join(",", ev.ToArray()) + " log=" + log.Count;
                m2.Tick(null, null, null, 5); m2.Tick(null, null, null, 8);
                if (string.Join(",", ev.ToArray()) != "x,y,ly" || log.Count != 2) return "throwing subscriber (left): " + string.Join(",", ev.ToArray()) + " log=" + log.Count;

                // T-198: the marker and the stamp of the loaded file
                {
                    var m4 = new MissionLifecycle(log.Add);
                    string got = null; int stampCalls = 0;
                    m4.MarkerOf = () => "/marker.sav";
                    m4.StampOf = p => { stampCalls++; return new SavStamp { ElapsedS = 7.5, Src = "sav" }; };
                    m4.Subscribe(1, "s", e => got = e.SavePath + "|" + e.MarkerPath + "|" + (e.Stamp == null ? "-" : e.Stamp.ElapsedS.ToString(System.Globalization.CultureInfo.InvariantCulture)), null);
                    m4.Tick(new object(), () => "/loaded.sav", () => 1.0, 0);
                    if (got != "/loaded.sav|/marker.sav|7.5" || stampCalls != 1) return "marker/stamp: " + got;
                    // a new mission (no path): the stamp is not read, the marker still arrives
                    got = null; stampCalls = 0;
                    m4.Tick(new object(), () => null, () => 1.0, 1);
                    if (got != "|/marker.sav|-" || stampCalls != 0) return "no path: " + got + " calls=" + stampCalls;
                    // a throwing stamp reader: Stamp = null, one log line, the others still run
                    var lg = new List<string>(); var m5 = new MissionLifecycle(lg.Add); string g5 = null;
                    m5.StampOf = p => { throw new InvalidOperationException("io"); };
                    m5.Subscribe(1, "s", e => g5 = e.SavePath + "|" + e.MarkerPath + "|" + (e.Stamp == null ? "-" : "x"), null);
                    m5.Tick(new object(), () => "/a.sav", () => 1.0, 0);
                    if (g5 != "/a.sav|/a.sav|-" || lg.Count != 1 || !lg[0].Contains("stamp")) return "throwing stamp: " + g5 + " log=" + lg.Count;
                }

                // the detector holds the mission weakly
                var m3 = new MissionLifecycle(null);
                WeakReference w = Enter(m3);
                m3.Tick(null, null, null, 0); m3.Tick(null, null, null, 3);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                if (w.IsAlive) return "the mission object must be collectable after leaving";
                return null;
            }
            catch (Exception e) { return "lifecycle: " + e.GetType().Name + ": " + e.Message; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference Enter(MissionLifecycle m)
        {
            var o = new object();
            m.Tick(o, null, null, 0);
            return new WeakReference(o);
        }
    }

    /// <summary>
    /// A mouse block that stays on for LimitS seconds without a break while the pointer is idle (no button down, not over a mod window)
    /// is stuck: the owner of the flag is gone. Tick says "release now" once per stuck period.
    /// </summary>
    internal sealed class InputBlockWatch
    {
        public const double LimitS = 30.0;
        private readonly Action<string> _log;
        private double _since = -1;
        private bool _fired;

        public InputBlockWatch(Action<string> log) { _log = log; }

        public bool Tick(bool blocked, bool pointerBusy, double realT, string owners)
        {
            if (!blocked || pointerBusy) { _since = -1; _fired = false; return false; }
            if (_since < 0) { _since = realT; return false; }
            if (_fired || realT - _since < LimitS) return false;
            _fired = true;
            if (_log != null) _log("input block stuck for " + (int)(realT - _since) + " s with an idle pointer, released: " + (owners ?? "?"));
            return true;
        }

        public static string SelfTest()
        {
            var log = new List<string>();
            var w = new InputBlockWatch(log.Add);
            if (w.Tick(true, false, 0, "asw")) return "first tick";
            if (w.Tick(true, false, 29, "asw")) return "29 s must not release";
            if (!w.Tick(true, false, 31, "asw,cap")) return "31 s idle must release";
            if (log.Count != 1 || !log[0].Contains("asw,cap")) return "one log line with owners";
            if (w.Tick(true, false, 40, "asw,cap") || log.Count != 1) return "once per stuck period";
            if (w.Tick(false, false, 41, "")) return "block off";
            if (w.Tick(true, false, 42, "x")) return "restart after off";
            if (w.Tick(true, false, 60, "x")) return "18 s after restart";
            if (w.Tick(true, true, 70, "x")) return "busy pointer resets";
            if (w.Tick(true, false, 71, "x")) return "count restarts after busy";
            if (w.Tick(true, false, 100, "x")) return "29 s after busy";
            if (!w.Tick(true, false, 102, "x")) return "31 s after busy must release";
            return null;
        }
    }
}
