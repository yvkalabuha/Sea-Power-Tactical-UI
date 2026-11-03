using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 LZ / ADR-0072 s.4.2, s.4.7: localization geometry outside the spook radius and the lost-contact ring (ADR-0076). Frame: X north, Y east,
    // origin = the (dead-reckoned) estimate of the contact. Pure, no Unity.
    internal static class AswLocalizeMath
    {
        /// <summary>Spook radius: the radius the submarine flees from a buoy / helicopter + the margin (2.0 + 0.3).</summary>
        public static double SpookR(double subSpookNm, double spookMarginNm) { return subSpookNm + spookMarginNm; }

        private static NmPt Polar(double r, double courseDeg)
        {
            double a = (double.IsNaN(courseDeg) ? 0 : courseDeg) * Math.PI / 180.0;
            return new NmPt(r * Math.Cos(a), r * Math.Sin(a));
        }

        /// <summary>
        /// Four passive buoys at r = max(rSpook, 0.8R) around <paramref name="shift"/>; when R &lt; r + 0.3 the cross on r hears nothing:
        /// r = 0.8R and spook = true (a deliberate cross inside the spook radius).
        /// </summary>
        public static List<NmPt> SpookCross(double rSpook, double rBuoy, NmPt shift, out double r, out bool spook)
        {
            r = Math.Max(rSpook, 0.8 * rBuoy);
            spook = false;
            if (rBuoy < r + 0.3) { r = 0.8 * rBuoy; spook = true; }
            var res = new List<NmPt>();
            for (int k = 0; k < 4; k++) { var p = Polar(r, 90.0 * k); res.Add(new NmPt(shift.X + p.X, shift.Y + p.Y)); }
            return res;
        }

        /// <summary>The active buoy: r ahead along the course (no course -> north).</summary>
        public static NmPt ActiveAhead(double r, double courseDeg) { return Polar(r, courseDeg); }

        /// <summary>Dip point of the holder: r from the estimate, abeam of the course; side +1 right, -1 left (alternate between calls).</summary>
        public static NmPt DipOffset(double r, double courseDeg, int side)
        {
            double c = double.IsNaN(courseDeg) ? 0 : courseDeg;
            return Polar(r, c + (side < 0 ? -90.0 : 90.0));
        }

        /// <summary>Dead reckoning of the estimate: speed x seconds along the course (unknown course or speed -> no shift).</summary>
        public static NmPt Lead(double courseDeg, double speedKn, double seconds)
        {
            if (double.IsNaN(courseDeg) || speedKn <= 0 || seconds <= 0) return new NmPt(0, 0);
            return Polar(speedKn * seconds / 3600.0, courseDeg);
        }

        /// <summary>R_FOC = v (lost + fly), v the last track speed clamped to [3; 30] kn, unknown -> defaultKn.</summary>
        public static double FocR(double vKn, double lostS, double flyS, double defaultKn)
        {
            double v = (double.IsNaN(vKn) || vKn <= 0) ? defaultKn : vKn;
            v = Math.Min(30, Math.Max(3, v));
            return v * (Math.Max(0, lostS) + Math.Max(0, flyS)) / 3600.0;
        }

        /// <summary>
        /// Lost-contact ring of radius r, points every <= spacingNm, starting north, shifted along the course by <paramref name="shiftNm"/>
        /// (default r / 2, i.e. about v t / 2), plus one point ahead on the course just beyond the ring. centreShift = the applied shift.
        /// </summary>
        public static List<NmPt> FocRing(double r, double spacingNm, double courseDeg, out NmPt centreShift, double shiftNm = double.NaN)
        {
            double sh = double.IsNaN(shiftNm) ? r / 2 : shiftNm;
            centreShift = double.IsNaN(courseDeg) ? new NmPt(0, 0) : Polar(sh, courseDeg);
            var res = new List<NmPt>();
            if (r <= 0) return res;
            int n = spacingNm > 0 ? Math.Max(3, (int)Math.Ceiling(2 * Math.PI * r / spacingNm - 1e-9)) : 8;
            for (int i = 0; i < n; i++)
            {
                var p = Polar(r, 360.0 * i / n);
                res.Add(new NmPt(centreShift.X + p.X, centreShift.Y + p.Y));
            }
            var ahead = Polar(r + Math.Max(spacingNm, 0), courseDeg);
            res.Add(new NmPt(centreShift.X + ahead.X, centreShift.Y + ahead.Y));
            return res;
        }

        public static string SelfTest() { return SelfTestSpook() ?? SelfTestFoc(); }

        private static string SelfTestSpook()
        {
            if (Math.Abs(SpookR(2.0, 0.3) - 2.3) > 1e-9) return "spook r";
            double r; bool sp;
            var c = SpookCross(2.3, 2.70, new NmPt(0, 0), out r, out sp);
            if (sp || Math.Abs(r - 2.3) > 1e-9 || c.Count != 4) return "cross ssq53";
            foreach (var p in c) if (Math.Sqrt(p.X * p.X + p.Y * p.Y) < 2.3 - 1e-9) return "cross inside spook";
            SpookCross(2.3, 1.1, new NmPt(0, 0), out r, out sp);
            if (!sp || Math.Abs(r - 0.88) > 1e-9) return "cross ssq41";
            var n = ActiveAhead(2.3, double.NaN);
            if (Math.Abs(n.X - 2.3) > 1e-9 || Math.Abs(n.Y) > 1e-9) return "active north";
            return null;
        }

        private static string SelfTestFoc()
        {
            if (Math.Abs(FocR(100, 600, 600, 5) - 10) > 1e-9) return "foc clamp high";
            if (Math.Abs(FocR(0, 600, 600, 5) - 5 * 1200 / 3600.0) > 1e-9 || Math.Abs(FocR(1, 600, 600, 5) - 3 * 1200 / 3600.0) > 1e-9) return "foc default / low";
            NmPt sh;
            var ring = FocRing(6, 4.32, 90, out sh);
            if (ring.Count != 10) return "foc ring count " + ring.Count;
            return null;
        }
    }
}
