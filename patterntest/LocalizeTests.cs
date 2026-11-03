using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 LZ: cross-spook, lost-ring (pure part).
    internal static class LocalizeTests
    {
        private static bool Near(double a, double b, double e = 1e-6) { return Math.Abs(a - b) <= e; }
        private static double Len(NmPt p) { return Math.Sqrt(p.X * p.X + p.Y * p.Y); }

        public static string Run()
        {
            // LZ1 (L4)
            double rs = AswLocalizeMath.SpookR(2.0, 0.3);
            if (!Near(rs, 2.3)) return "spook r " + rs;
            double r; bool spook;
            var cross = AswLocalizeMath.SpookCross(rs, 2.70, new NmPt(0, 0), out r, out spook);
            if (spook || !Near(r, 2.3) || cross.Count != 4) return "cross ssq53 " + r + " " + spook;
            foreach (var p in cross) if (Len(p) < rs - 1e-9) return "cross point inside the spook radius";
            var shifted = AswLocalizeMath.SpookCross(rs, 2.70, new NmPt(5, -3), out r, out spook);
            foreach (var p in shifted) if (!Near(Len(new NmPt(p.X - 5, p.Y + 3)), 2.3)) return "cross shift";
            AswLocalizeMath.SpookCross(rs, 1.1, new NmPt(0, 0), out r, out spook);
            if (!spook || !Near(r, 0.88)) return "cross ssq41 " + r + " " + spook;
            var big = AswLocalizeMath.SpookCross(rs, 6, new NmPt(0, 0), out r, out spook);
            if (spook || !Near(r, 4.8)) return "cross big R " + r;
            var north = AswLocalizeMath.ActiveAhead(2.3, double.NaN);
            if (!Near(north.X, 2.3) || !Near(north.Y, 0)) return "active ahead without a course";
            var east = AswLocalizeMath.ActiveAhead(2.3, 90);
            if (!Near(east.X, 0) || !Near(east.Y, 2.3)) return "active ahead east";
            var right = AswLocalizeMath.DipOffset(2.3, 0, 1); var left = AswLocalizeMath.DipOffset(2.3, 0, -1);
            if (!Near(right.Y, 2.3) || !Near(left.Y, -2.3) || !Near(Len(right), 2.3)) return "dip offset sides";
            if (Len(AswLocalizeMath.DipOffset(2.3, double.NaN, 1)) < 2.3 - 1e-9) return "dip offset without a course";
            var ld = AswLocalizeMath.Lead(90, 6, 1800);
            if (!Near(ld.Y, 3) || !Near(ld.X, 0)) return "lead";
            if (Len(AswLocalizeMath.Lead(double.NaN, 6, 1800)) != 0 || Len(AswLocalizeMath.Lead(90, 0, 1800)) != 0) return "lead unknown";
            // LZ2 (L5)
            if (!Near(AswLocalizeMath.FocR(100, 600, 600, 5), 10)) return "foc clamp 30 kn";
            if (!Near(AswLocalizeMath.FocR(1, 600, 600, 5), 3 * 1200 / 3600.0)) return "foc clamp 3 kn";
            if (!Near(AswLocalizeMath.FocR(0, 600, 600, 5), 5 * 1200 / 3600.0) || !Near(AswLocalizeMath.FocR(double.NaN, 600, 600, 5), 5 * 1200 / 3600.0)) return "foc default speed";
            if (!Near(AswLocalizeMath.FocR(12, 600, 300, 5), 12 * 900 / 3600.0)) return "foc plain";
            NmPt sh;
            double step = 1.6 * 2.7;
            var ring = AswLocalizeMath.FocRing(8, step, 90, out sh);
            if (!Near(sh.Y, 4) || !Near(sh.X, 0)) return "foc shift " + sh.X + "," + sh.Y;
            int n = (int)Math.Ceiling(2 * Math.PI * 8 / step - 1e-9);
            if (ring.Count != n + 1) return "foc ring count " + ring.Count + " vs " + (n + 1);
            for (int i = 0; i < n; i++) if (!Near(Len(new NmPt(ring[i].X - sh.X, ring[i].Y - sh.Y)), 8)) return "foc ring radius";
            for (int i = 0; i < n; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % n];
                if (Len(new NmPt(a.X - b.X, a.Y - b.Y)) > step + 1e-6) return "foc ring spacing";
            }
            var ahead = ring[n];
            if (!(ahead.Y - sh.Y > 8) || !Near(ahead.X - sh.X, 0)) return "foc one ahead of the ring";
            AswLocalizeMath.FocRing(8, step, 0, out sh, 3);
            if (!Near(sh.X, 3) || !Near(sh.Y, 0)) return "foc explicit shift";
            if (AswLocalizeMath.FocRing(0, step, 0, out sh).Count != 0) return "foc zero";
            return AswLocalizeMath.SelfTest();
        }
    }
}
