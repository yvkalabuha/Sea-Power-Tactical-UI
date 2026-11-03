// T-361 (ADR-0136): pure geometry of the dev `force` scenarios (no Unity, no reflection; captest selftest "qaforce"). Same flat metric as CapFfMath (AswMath.DistNm / BearingDeg / Offset).
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class QaForceMath
    {
        /// <summary>A point distNm from p along trueBearingDeg.</summary>
        public static GeoPt Offset(GeoPt p, double trueBearingDeg, double distNm) { return AswMath.Offset(p, trueBearingDeg, distNm); }

        /// <summary>A point on the way shooter -> target, aheadNm short of the target, turned by offDeg around the SHOOTER (so the cone angle seen from the shooter is offDeg).
        /// offDeg 0 = on the line inside the friendly-fire cone; 30 = clearly outside it (the hold of the guard must not apply). A target nearer than aheadNm gives the point of the shooter turned.</summary>
        public static GeoPt LeadPoint(GeoPt shooter, GeoPt target, double aheadNm, double offDeg)
        {
            double dt = AswMath.DistNm(shooter, target);
            double brg = AswMath.BearingDeg(shooter, target);
            return AswMath.Offset(shooter, brg + offDeg, Math.Max(0.1, dt - aheadNm));
        }

        /// <summary>The cone angle (deg) of p seen from the shooter against the shooter -> target line, and the distance p -> target (nm).</summary>
        public static double ConeDeg(GeoPt shooter, GeoPt target, GeoPt p, out double toTargetNm)
        {
            toTargetNm = AswMath.DistNm(p, target);
            if (AswMath.DistNm(shooter, target) < 1e-9 || AswMath.DistNm(shooter, p) < 1e-9) return 0;
            double diff = Math.Abs(AswMath.BearingDeg(shooter, target) - AswMath.BearingDeg(shooter, p)) % 360.0;
            return diff > 180 ? 360 - diff : diff;
        }

        /// <summary>The waypoint list ([lat, lon] pairs) with its last point moved; null for an empty list. The input is not changed.</summary>
        public static List<double[]> ShiftLast(IList<double[]> wps, double trueBearingDeg, double distNm)
        {
            if (wps == null || wps.Count == 0) return null;
            var res = new List<double[]>(wps.Count);
            foreach (var w in wps) res.Add(new[] { w[0], w[1] });
            var last = res[res.Count - 1];
            var moved = Offset(new GeoPt(last[0], last[1]), trueBearingDeg, distNm);
            res[res.Count - 1] = new[] { moved.Lat, moved.Lon };
            return res;
        }

        /// <summary>steer_hostile point: stopNm from the helicopter on the way to the hostile (away=false) or 60 nm from the helicopter on the far side of the hostile (away=true).</summary>
        public static GeoPt SteerPoint(GeoPt helo, GeoPt hostile, double stopNm, bool away)
        {
            double brg = AswMath.BearingDeg(helo, hostile);
            return AswMath.Offset(helo, brg, away ? 60.0 : stopNm);
        }

        // ---------- selftest ----------
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private static GeoPt At(double eastNm, double northNm) { return new GeoPt(10.5 + northNm / 60.0, 11.0 + eastNm / 60.0); }

        public static string SelfTest()
        {
            _err = null;
            double to;
            // Offset: 90 deg is east, 60 nm on the equator is one degree
            var o = Offset(new GeoPt(0, 10), 90, 60);
            True("offset east", Math.Abs(o.Lat) < 1e-9 && Math.Abs(o.Lon - 11) < 1e-6);
            var n = Offset(new GeoPt(0, 10), 0, 30);
            True("offset north", Math.Abs(n.Lat - 0.5) < 1e-9 && Math.Abs(n.Lon - 10) < 1e-9);
            // LeadPoint: off 0 = in the line, 1 nm short of the target, inside the cone of the guard
            var s = At(0, 0); var t = At(0, 10);
            var p0 = LeadPoint(s, t, 1.0, 0);
            double c0 = ConeDeg(s, t, p0, out to);
            True("lead on line", c0 < 0.01 && Math.Abs(to - 1.0) < 1e-6);
            True("lead in the guard cone", CapFfMath.InCone(s, t, p0, out to, out c0));
            // off 30: the cone angle is 30, outside the 10 deg cone and the 2 nm radius
            var p30 = LeadPoint(s, t, 1.0, 30);
            double c30 = ConeDeg(s, t, p30, out to);
            True("lead off cone", Math.Abs(c30 - 30) < 0.01 && to > 2.0);
            True("lead off not in guard cone", !CapFfMath.InCone(s, t, p30, out to, out c30));
            // a target nearer than aheadNm still gives a point
            var pn = LeadPoint(s, At(0, 0.5), 1.0, 0);
            True("lead near target", AswMath.DistNm(s, pn) > 0 && AswMath.DistNm(s, pn) < 0.2);
            // ConeDeg symmetric, degenerate shooter == target
            True("cone left=right", Math.Abs(ConeDeg(s, t, At(1, 5), out to) - ConeDeg(s, t, At(-1, 5), out to)) < 1e-9);
            True("cone degenerate", ConeDeg(s, s, t, out to) == 0);
            // ShiftLast changes only the last point and works on a copy
            var wps = new List<double[]> { new[] { 10.0, 20.0 }, new[] { 11.0, 21.0 } };
            var sh = ShiftLast(wps, 90, 6);
            True("shift copy", sh != null && sh != wps && wps[1][0] == 11.0 && wps[1][1] == 21.0);
            True("shift first same", sh[0][0] == 10.0 && sh[0][1] == 20.0);
            True("shift last", sh.Count == 2 && Math.Abs(sh[1][0] - 11.0) < 1e-6 && sh[1][1] > 21.0
                && Math.Abs(AswMath.DistNm(new GeoPt(11, 21), new GeoPt(sh[1][0], sh[1][1])) - 6) < 0.01);
            True("shift empty", ShiftLast(new List<double[]>(), 0, 1) == null && ShiftLast(null, 0, 1) == null);
            // SteerPoint: stop_nm from the helicopter on the way to the hostile; away = 60 nm
            var helo = At(0, 0); var enemy = At(30, 0);
            var sp = SteerPoint(helo, enemy, 4, false);
            True("steer stop", Math.Abs(AswMath.DistNm(helo, sp) - 4) < 0.01 && Math.Abs(AswMath.BearingDeg(helo, sp) - 90) < 0.1);
            var sa = SteerPoint(helo, enemy, 4, true);
            True("steer away", Math.Abs(AswMath.DistNm(helo, sa) - 60) < 0.05);
            return _err;
        }
    }
}
