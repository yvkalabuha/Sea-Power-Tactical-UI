using System;
using System.Globalization;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-336: when the heartbeat may start (thread sptui-hb, ticks, recorders). Pure; main thread only.</summary>
    internal sealed class StartupGate
    {
        public const double MenuSettleS = 20.0;     // real time after the main-menu scene load
        public const int MenuSettleFrames = 120;    // ... and this many frames (a stalled menu build does not count)
        public const double MaxWaitS = 300.0;       // no menu scene seen (hooked too late): open anyway after this real time
        public const int MaxWaitFrames = 300;       //   ... and this many frames since the first Feed

        private bool _menu, _fed;
        private double _menuT, _firstT;
        private long _menuFrame, _firstFrame;

        public bool Open { get; private set; }
        public string Why { get; private set; }

        /// <summary>Same rule as DevStartGate.IsReadyScene.</summary>
        public static bool IsMenuScene(int buildIndex) { return buildIndex == 1 || buildIndex == 2; }

        /// <summary>First call wins; later calls are ignored.</summary>
        public void OnMenuLoaded(double realT, long frame)
        {
            if (_menu) return;
            _menu = true; _menuT = realT; _menuFrame = frame;
        }

        /// <summary>True exactly once: on the transition to open.</summary>
        public bool Feed(double realT, long frame, bool inMission)
        {
            if (Open) return false;
            if (!_fed) { _fed = true; _firstT = realT; _firstFrame = frame; }
            string why = null;
            if (inMission) why = "mission";
            else if (_menu && realT - _menuT >= MenuSettleS && frame - _menuFrame >= MenuSettleFrames) why = "menu_settled";
            else if (realT - _firstT >= MaxWaitS && frame - _firstFrame >= MaxWaitFrames) why = "timeout";
            if (why == null) return false;
            Open = true; Why = why;
            return true;
        }

        public static string SelfTest()
        {
            var g = new StartupGate();
            if (g.Feed(1, 1, false) || g.Open) return "gate: opened before menu";
            if (!g.Feed(2, 2, true) || g.Why != "mission") return "gate: mission";
            if (g.Feed(3, 3, true)) return "gate: second open";
            g = new StartupGate();
            g.Feed(0, 0, false);
            g.OnMenuLoaded(30, 500);
            g.OnMenuLoaded(40, 900);
            if (g.Feed(49, 700, false)) return "gate: early time";
            if (g.Feed(50, 619, false)) return "gate: few frames";
            if (!g.Feed(50, 620, false) || g.Why != "menu_settled") return "gate: menu_settled";
            g = new StartupGate();
            g.Feed(0, 0, false);
            if (g.Feed(300, 10, false)) return "gate: timeout with few frames";
            g = new StartupGate();
            g.Feed(0, 0, false);
            if (!g.Feed(300, 300, false) || g.Why != "timeout") return "gate: timeout";
            if (IsMenuScene(0) || !IsMenuScene(1) || !IsMenuScene(2)) return "gate: IsMenuScene";
            return null;
        }
    }

    /// <summary>T-336: three startup marks for LogOutput. Pure; main thread only.</summary>
    internal sealed class StartupProbe
    {
        public const string FirstUpdate = "first_update", MenuLoaded = "menu_loaded", MenuFirstFrame = "menu_first_frame";
        private bool _first, _menu, _after, _menuFrameDone;

        public bool Done { get { return _first && _menu && _menuFrameDone; } }

        /// <summary>FirstUpdate on the 1st call; MenuFirstFrame on the 1st call after OnSceneLoaded(true); else null.</summary>
        public string OnUpdate()
        {
            if (!_first) { _first = true; return FirstUpdate; }
            if (_menu && !_menuFrameDone) { _menuFrameDone = true; return MenuFirstFrame; }
            return null;
        }

        public string OnSceneLoaded(bool menu)
        {
            if (!menu || _menu) return null;
            _menu = true;
            return MenuLoaded;
        }

        public static string Line(string point, double realT, long frame, int gc0, double monoUsedMb)
        {
            var c = CultureInfo.InvariantCulture;
            return "startup: point=" + point + " realtime=" + realT.ToString("F1", c) + " frame=" + frame.ToString(c) + " gc0=" + gc0.ToString(c) +
                   " mono_used=" + (monoUsedMb < 0 ? "?" : monoUsedMb.ToString("F1", c) + "MB");
        }

        public static string SelfTest()
        {
            var p = new StartupProbe();
            if (p.OnUpdate() != FirstUpdate) return "probe: first_update";
            if (p.OnUpdate() != null) return "probe: second update";
            if (p.OnSceneLoaded(false) != null) return "probe: non-menu";
            if (p.Done) return "probe: done early";
            if (p.OnSceneLoaded(true) != MenuLoaded) return "probe: menu_loaded";
            if (p.OnSceneLoaded(true) != null) return "probe: menu twice";
            if (p.Done) return "probe: done before first frame";
            if (p.OnUpdate() != MenuFirstFrame) return "probe: menu_first_frame";
            if (p.OnUpdate() != null) return "probe: extra update";
            if (!p.Done) return "probe: not done";
            var old = CultureInfo.CurrentCulture;
            try
            {
                var ru = (CultureInfo)CultureInfo.InvariantCulture.Clone();   // ru-RU is unavailable in invariant-globalization stands
                ru.NumberFormat.NumberDecimalSeparator = ",";
                CultureInfo.CurrentCulture = ru;
                string l = Line("first_update", 12.34, 456, 78, 53.2);
                if (l != "startup: point=first_update realtime=12.3 frame=456 gc0=78 mono_used=53.2MB") return "probe: line " + l;
                if (!Line("x", 1, 1, 1, -1).EndsWith("mono_used=?")) return "probe: unknown mono";
            }
            finally { CultureInfo.CurrentCulture = old; }
            return null;
        }
    }

    /// <summary>T-336: cadence of the /proc read on sptui-hb. Pure; one reader thread.</summary>
    internal sealed class ProcCadence
    {
        public const double PeriodS = 30.0, MinGapS = 2.0;
        private volatile bool _requested;
        private bool _read;
        private double _last;

        /// <summary>Any thread: dev DiagJson asks for a fresh sample.</summary>
        public void Request() { _requested = true; }

        public bool Due(double nowS)
        {
            bool due = !_read || (_requested && nowS - _last >= MinGapS) || nowS - _last >= PeriodS;
            if (!due) return false;
            _read = true; _last = nowS; _requested = false;
            return true;
        }

        public static string SelfTest()
        {
            var c = new ProcCadence();
            if (!c.Due(100)) return "cadence: first";
            if (c.Due(101)) return "cadence: 1s";
            if (!c.Due(131)) return "cadence: 31s";
            c.Request();
            if (c.Due(132)) return "cadence: request too early";
            if (!c.Due(133)) return "cadence: request";
            if (c.Due(135.5)) return "cadence: request not cleared";
            return null;
        }
    }
}
