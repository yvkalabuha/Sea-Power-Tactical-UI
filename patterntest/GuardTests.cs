using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-176 F: v3-filter (pure part): Classify, thresholds, legs through circles.
    internal static class GuardTests
    {
        private static bool Near(double a, double b, double e = 1e-6) { return Math.Abs(a - b) <= e; }

        public static string Run()
        {
            var o = new GeoPt(35, 20);
            double wb = AswRouteGuard.MinWaterBuoyM(30, 300), wd = AswRouteGuard.MinWaterDipM(30);
            if (!Near(wb, 30.432, 1e-3) || !Near(AswRouteGuard.MinWaterBuoyM(10, 300), 30.432, 1e-3) || !Near(AswRouteGuard.MinWaterBuoyM(30, 50), 30)) return "min water buoy " + wb;
            if (!Near(AswRouteGuard.MinWaterBuoyM(30, 0), 30) || !Near(AswRouteGuard.MinWaterBuoyM(10, -5), 10)) return "min water buoy unknown depth";
            if (!Near(wd, 30.2) || !Near(AswRouteGuard.MinWaterDipM(50), 50)) return "min water dip";
            var seq = new[] { 0.0, -12, -25, -45 };
            var want = new[] { AswSkip.Land, AswSkip.Shallow, AswSkip.Shallow, AswSkip.None };
            for (int i = 0; i < seq.Length; i++)
            {
                var got = AswRouteGuard.Classify(o, false, seq[i], wb, null, 5, null);
                if (got != want[i]) return "buoy at " + seq[i] + " m -> " + got;
            }
            if (AswRouteGuard.Classify(o, true, -25, wd, null, 5, null) != AswSkip.Shallow) return "dip at -25";
            if (AswRouteGuard.Classify(o, true, -45, wd, null, 5, null) != AswSkip.None) return "dip at -45";
            if (AswRouteGuard.Classify(o, false, 150, wb, null, 5, null) != AswSkip.Land) return "high land";
            if (AswRouteGuard.Classify(o, false, double.NaN, wb, null, 5, null) != AswSkip.None) return "unknown height";
            var hz = new List<AswHazard> { new AswHazard(AswMath.Offset(o, 90, 3), 0) };
            if (AswRouteGuard.Classify(o, false, -200, wb, hz, 5, null) != AswSkip.Hazard) return "hazard default range";
            if (AswRouteGuard.Classify(o, false, -200, wb, new List<AswHazard> { new AswHazard(AswMath.Offset(o, 90, 3), 2) }, 5, null) != AswSkip.None) return "hazard class range";
            if (AswRouteGuard.Classify(o, false, -200, wb, null, 5, p => true) != AswSkip.Air) return "air";
            if (AswRouteGuard.Classify(o, false, -200, wb, hz, 5, p => true) != AswSkip.Hazard) return "hazard before air";
            if (AswRouteGuard.Classify(o, false, 5, wb, hz, 5, p => true) != AswSkip.Land) return "land before hazard";
            if (AswRouteGuard.Classify(o, false, -200, wb, null, 5, p => false) != AswSkip.None) return "air not blocked";
            // F2: segments through circles
            var a = AswMath.Offset(o, 0, 0); var b = AswMath.Offset(o, 0, 20);
            var c = AswMath.Offset(AswMath.Offset(o, 0, 10), 90, 2);
            if (!Near(AswRouteGuard.SegDistNm(a, b, c), 2, 1e-6)) return "seg dist " + AswRouteGuard.SegDistNm(a, b, c);
            if (!Near(AswRouteGuard.SegDistNm(a, b, AswMath.Offset(b, 0, 3)), 3, 1e-6)) return "seg dist beyond the end";
            if (!Near(AswRouteGuard.SegDistNm(a, a, AswMath.Offset(a, 90, 4)), 4, 1e-6)) return "seg dist zero length";
            var circles = new List<AswCircle> { new AswCircle { C = c, RNm = 3, Why = AswSkip.Air } };
            var pts = new List<GeoPt> { AswMath.Offset(o, 0, 5), b, AswMath.Offset(o, 0, 40) };
            if (AswRouteGuard.FirstBlockedLeg(o, pts, circles) != 1) return "blocked leg " + AswRouteGuard.FirstBlockedLeg(o, pts, circles);
            var touch = new List<AswCircle> { new AswCircle { C = c, RNm = 2, Why = AswSkip.Hazard } };
            if (AswRouteGuard.FirstBlockedLeg(o, pts, touch) != -1) return "touching the edge is not a block";
            var far = new List<AswCircle> { new AswCircle { C = AswMath.Offset(o, 90, 30), RNm = 3, Why = AswSkip.Hazard } };
            if (AswRouteGuard.FirstBlockedLeg(o, pts, far) != -1 || AswRouteGuard.FirstBlockedLeg(o, pts, null) != -1 || AswRouteGuard.FirstBlockedLeg(o, null, circles) != -1) return "no block";
            return null;
        }
    }
}
