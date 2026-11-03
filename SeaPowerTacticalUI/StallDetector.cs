using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-154 F2: pure main-thread stall detector. A background timer feeds it the frame counter once a second;
    /// it reports "stuck" once after the counter stayed the same for limitS, and again only after the counter has moved.
    /// </summary>
    internal sealed class StallDetector
    {
        private readonly double _limitS;
        private long _frame = long.MinValue;
        private double _since;
        private bool _reported;

        public StallDetector(double limitS) { _limitS = limitS; }

        /// <summary>True exactly once per stall (frame unchanged for limitS seconds).</summary>
        public bool Tick(long frame, double nowS)
        {
            if (frame != _frame) { _frame = frame; _since = nowS; _reported = false; return false; }
            if (_reported || nowS - _since < _limitS) return false;
            _reported = true;
            return true;
        }

        public double StuckFor(double nowS) { return nowS - _since; }

        public static string SelfTest()
        {
            var d = new StallDetector(8.0);
            if (d.Tick(1, 0)) return "watchdog: first tick must not report";
            for (int t = 1; t < 8; t++) if (d.Tick(1, t)) return "watchdog: reported early at " + t;
            if (!d.Tick(1, 8)) return "watchdog: no report after 8 s";
            for (int t = 9; t < 40; t++) if (d.Tick(1, t)) return "watchdog: reported twice";
            if (d.Tick(2, 40)) return "watchdog: frame change must reset";
            for (int t = 41; t < 48; t++) if (d.Tick(2, t)) return "watchdog: early after reset";
            if (!d.Tick(2, 48.5)) return "watchdog: no second report after reset";
            if (Math.Abs(d.StuckFor(50) - 10.0) > 1e-9) return "watchdog: StuckFor";
            return null;
        }
    }
}
