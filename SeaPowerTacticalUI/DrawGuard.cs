using System;

namespace VoiceBotBridge
{
    /// <summary>Receiver of the watchdog breadcrumb (StageCrumb writes it to a memory-mapped file that survives a frozen runtime).</summary>
    internal interface IStageSink
    {
        void Stage(string stage);
        void Frame(long frameNo);
        void Mark(int kind);
    }

    /// <summary>T-154 F1: pure guards for overlay line drawing (no Unity). Non-finite or huge segments must never loop forever.</summary>
    internal static class DrawGuard
    {
        private static volatile string _stage = "";

        /// <summary>Breadcrumb for the main-thread watchdog: "&lt;feature&gt;/&lt;phase&gt;/&lt;label&gt;". Every change also goes to Sink (the mapped stage file).</summary>
        public static string Stage
        {
            get { return _stage; }
            set { _stage = value; var s = Sink; if (s != null) s.Stage(value); }
        }

        /// <summary>Hang triage: the mapped breadcrumb file (StageCrumb), set by HeartbeatFeature; null = off. Main thread only.</summary>
        internal static IStageSink Sink;

        public const int MarkFrame = 1, MarkStage = 2, MarkUpdateEnd = 3, MarkGuiEnd = 4, MarkClosed = 5;

        /// <summary>UiCore: a new Update frame starts.</summary>
        public static void FrameStart(long frameNo) { var s = Sink; if (s != null) s.Frame(frameNo); }

        /// <summary>UiCore: all modules are through Update (MarkUpdateEnd) or one OnGUI pass (MarkGuiEnd).</summary>
        public static void Mark(int kind) { var s = Sink; if (s != null) s.Mark(kind); }

        public static bool Finite(float a, float b, float c, float d)
        {
            return IsFinite(a) && IsFinite(b) && IsFinite(c) && IsFinite(d);
        }

        private static bool IsFinite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

        /// <summary>Liang-Barsky clip of a segment to [-pad, w+pad] x [-pad, h+pad]. False when not finite or fully outside.</summary>
        public static bool ClipSegment(ref float ax, ref float ay, ref float bx, ref float by, float w, float h, float pad)
        {
            if (!Finite(ax, ay, bx, by) || !IsFinite(w) || !IsFinite(h) || !IsFinite(pad)) return false;
            double x0 = ax, y0 = ay, dx = (double)bx - ax, dy = (double)by - ay;
            double xmin = -pad, xmax = w + pad, ymin = -pad, ymax = h + pad;
            double t0 = 0, t1 = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x0 - xmin, xmax - x0, y0 - ymin, ymax - y0 };
            for (int i = 0; i < 4; i++)
            {
                if (p[i] == 0)
                {
                    if (q[i] < 0) return false;
                    continue;
                }
                double r = q[i] / p[i];
                if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            float nax = (float)(x0 + t0 * dx), nay = (float)(y0 + t0 * dy);
            float nbx = (float)(x0 + t1 * dx), nby = (float)(y0 + t1 * dy);
            if (!Finite(nax, nay, nbx, nby)) return false;
            ax = nax; ay = nay; bx = nbx; by = nby;
            return true;
        }

        /// <summary>Number of dashes along a segment of length len: 0 for non-finite or len &lt; 1, capped at max.</summary>
        public static int DashCount(float len, float on, float off, int max)
        {
            if (!IsFinite(len) || !IsFinite(on) || !IsFinite(off) || len < 1f) return 0;
            float step = on + off;
            if (!(step > 1e-3f) || max <= 0) return 0;
            double n = Math.Ceiling(len / (double)step);
            return n > max ? max : (int)n;
        }

        public static string SelfTest()
        {
            float ax = 0, ay = 0, bx = float.PositiveInfinity, by = 0;
            if (ClipSegment(ref ax, ref ay, ref bx, ref by, 2560, 1371, 16)) return "inf not rejected";
            ax = float.NaN; ay = 0; bx = 10; by = 0;
            if (ClipSegment(ref ax, ref ay, ref bx, ref by, 2560, 1371, 16)) return "nan not rejected";
            ax = -1e9f; ay = 500; bx = 1e9f; by = 500;
            if (!ClipSegment(ref ax, ref ay, ref bx, ref by, 2560, 1371, 16)) return "long line rejected";
            if (Math.Abs(ax + 16) > 0.5f || Math.Abs(bx - 2576) > 0.5f || Math.Abs(ay - 500) > 0.01f) return "long line clip " + ax + "," + bx;
            ax = 3000; ay = 10; bx = 4000; by = 20;
            if (ClipSegment(ref ax, ref ay, ref bx, ref by, 2560, 1371, 16)) return "outside segment kept";
            ax = 10; ay = 10; bx = 100; by = 50;
            if (!ClipSegment(ref ax, ref ay, ref bx, ref by, 2560, 1371, 16) || ax != 10 || bx != 100) return "inside segment changed";
            ax = -100; ay = -100; bx = 100; by = 100;
            if (!ClipSegment(ref ax, ref ay, ref bx, ref by, 50, 50, 0) || Math.Abs(ax) > 0.01f || Math.Abs(bx - 50) > 0.01f) return "diagonal clip";
            if (DashCount(float.PositiveInfinity, 8, 6, 512) != 0) return "dash inf";
            if (DashCount(float.NaN, 8, 6, 512) != 0) return "dash nan";
            if (DashCount(3e8f, 8, 6, 512) != 512) return "dash cap";
            if (DashCount(100, 8, 6, 512) != 8) return "dash 100 = " + DashCount(100, 8, 6, 512);
            if (DashCount(0.5f, 8, 6, 512) != 0) return "dash short";
            if (DashCount(100, 0, 0, 512) != 0) return "dash zero step";
            return null;
        }
    }
}
