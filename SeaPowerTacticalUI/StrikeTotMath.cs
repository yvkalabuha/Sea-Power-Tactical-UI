using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132: time on target (arrival T, launch time, the condition of the order, spread, the jammer window). Pure; times are mission seconds unless noted.
    internal static class StrikeTotMath
    {
        /// <summary>The rough time of flight from the live probes of 2026-10-31 (AGM-84D, vmax 540 kn): 2 s + R / (vmax * 528/540). vmax &lt;= 0 = 540 kn.</summary>
        public static double RoughTofS(double rangeNm, double vmaxKn)
        {
            double v = vmaxKn > 0 ? vmaxKn : 540;
            return StrikeCalib.TofBaseS + rangeNm / (v * StrikeCalib.TofSpeedFrac) * 3600.0;
        }

        /// <summary>T = now + max(ready + tof) + margin, not earlier than the readiness of the jammer (absolute time; 0 = none). readyPlusTofS are seconds from now.</summary>
        public static double ArriveT(IList<double> readyPlusTofS, double now, double marginS, double ewReadyT)
        {
            double mx = 0;
            if (readyPlusTofS != null) foreach (double v in readyPlusTofS) mx = Math.Max(mx, v);
            return Math.Max(now + mx + marginS, ewReadyT);
        }

        /// <summary>The turn onto the target: |heading - bearing| in [0; 180]; an unknown heading (&lt; 0) counts as 90 degrees.</summary>
        public static double DeltaPsi(double headingDeg, double bearingDeg)
        {
            if (headingDeg < 0 || double.IsNaN(headingDeg)) return 90;
            double d = Math.Abs(AswMath.Norm360(headingDeg - bearingDeg));
            return d > 180 ? 360 - d : d;
        }

        /// <summary>Seconds from the order to the missile leaving: TurnBaseS + dpsi / TurnRateDps (the probes of 2026-10-31 at 5000 ft: 66 deg = 8.0 s, 97 deg = 9.9 s = 16 deg/s; the strikes at 200 ft: 77-117 deg = 13-21 s = 8 deg/s) + the reserve TurnLeadS.</summary>
        public static double TurnLeadS(double deltaPsiDeg, StrikeParams p)
        {
            var q = p ?? new StrikeParams();
            return q.TurnBaseS + Math.Abs(deltaPsiDeg) / (q.TurnRateDps > 0 ? q.TurnRateDps : 11.5) + q.TurnLeadS;
        }

        /// <summary>The same with the turn rate of the aircraft (heavy carriers, StrikeCalib.TurnRateDps; rate &lt;= 0 = the parameter TurnRateDps).</summary>
        public static double TurnLeadS(double deltaPsiDeg, StrikeParams p, double rateDps)
        {
            var q = p ?? new StrikeParams();
            double rate = rateDps > 0 ? rateDps : (q.TurnRateDps > 0 ? q.TurnRateDps : 11.5);
            return q.TurnBaseS + Math.Abs(deltaPsiDeg) / rate + q.TurnLeadS;
        }

        /// <summary>E1b: the extra seconds that an aircraft needs for the way to a point distNm away, bearing dpsiSignedDeg off its heading (+ = to the right), when it first turns on a circle of radius v / rate and then flies straight
        /// (the Tu-22M2 at 537 kn turns at 4.3 deg/s, R = 2 nm: 180 degrees cost 43 s over the straight way). 0 for rate &lt;= 0 (not a heavy carrier).</summary>
        public static double ArcExtraS(double distNm, double dpsiSignedDeg, double speedKn, double rateDps)
        {
            if (rateDps <= 0 || speedKn <= 0 || distNm <= 0) return 0;
            double v = speedKn / 3600.0, R = v / (rateDps * Math.PI / 180.0), b = dpsiSignedDeg * Math.PI / 180.0;
            double s = b >= 0 ? -1 : 1;                              // +1 = a left (counter-clockwise) turn
            double tx = distNm * Math.Cos(b), ty = -distNm * Math.Sin(b), cx = 0, cy = s * R;
            double dc = Math.Sqrt((tx - cx) * (tx - cx) + (ty - cy) * (ty - cy)), path;
            if (dc <= R) path = distNm + 2 * Math.PI * R;            // the point is inside the circle: a turn first
            else
            {
                double len = Math.Sqrt(dc * dc - R * R), aT = Math.Atan2(ty - cy, tx - cx), off = Math.Acos(R / dc), a0 = Math.Atan2(-cy, -cx);
                double sw = s > 0 ? aT - off - a0 : a0 - (aT + off);
                sw %= 2 * Math.PI; if (sw < 0) sw += 2 * Math.PI;
                if (sw > 2 * Math.PI - 1e-9) sw = 0;
                path = R * sw + len;
            }
            return Math.Max(0, (path - distNm) / v);
        }

        /// <summary>The signed turn to a bearing: bearing - heading in (-180; 180]; an unknown heading gives 0.</summary>
        public static double SignedDeltaDeg(double headingDeg, double bearingDeg)
        {
            if (headingDeg < 0 || double.IsNaN(headingDeg)) return 0;
            double d = AswMath.Norm360(bearingDeg - headingDeg);
            return d > 180 ? d - 360 : d;
        }

        /// <summary>The time to leave the gate for the launch point (legNm away at kn): tLaunch - leg / kn. kn &lt;= 0 = 400 kn.</summary>
        public static double TGo(double tLaunch, double legNm, double kn) { return tLaunch - Math.Max(0, legNm) / (kn > 0 ? kn : 400) * 3600.0; }

        public static double TLaunch(double tArrive, double tofS) { return tArrive - tofS; }
        public static double TIssue(double tLaunch, double turnLeadS) { return tLaunch - turnLeadS; }

        /// <summary>The order is given when the missile would arrive on time (now + the turn + the flight from here >= T) and the range is inside the window of the missile.</summary>
        public static bool IssueNow(double now, double turnLeadS, double tofNowS, double tArrive, double distNm, double minNm, double maxNm)
        {
            if (distNm < minNm || distNm > maxNm) return false;
            return now + turnLeadS + tofNowS >= tArrive;
        }

        /// <summary>Spread of the arrivals, s (0 for fewer than two).</summary>
        public static double SpreadS(IList<double> arrivals)
        {
            if (arrivals == null || arrivals.Count < 2) return 0;
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double a in arrivals) { lo = Math.Min(lo, a); hi = Math.Max(hi, a); }
            return hi - lo;
        }

        /// <summary>The jammer window: from the first detection of the strike by the radars of the targets to the last arrival plus the hold.</summary>
        public static void EwWindow(double tFirstDetect, double tLastArrive, double holdS, out double tOn, out double tOff)
        {
            tOn = tFirstDetect;
            tOff = tLastArrive + holdS;
        }

        public static string SelfTest()
        {
            // the probe table of 2026-10-31: 59.0 nm -> 404 s, 36.4 nm -> 250 s (AGM-84D)
            if (Math.Abs(RoughTofS(59.0, 540) - 404) > 2 || Math.Abs(RoughTofS(36.4, 540) - 250) > 2) return "rough tof probes " + RoughTofS(59.0, 540) + " " + RoughTofS(36.4, 540);
            if (Math.Abs(RoughTofS(60, 0) - RoughTofS(60, 540)) > 1e-9) return "rough tof no speed";
            if (Math.Abs(RoughTofS(200, 2000) - (2 + 200.0 / (2000 * 528.0 / 540) * 3600)) > 0.01) return "rough tof as-4";
            if (ArriveT(new[] { 100.0, 400.0, 250.0 }, 1000, 30, 0) != 1430) return "arrive";
            if (ArriveT(new[] { 100.0 }, 1000, 30, 2000) != 2000) return "arrive jammer";
            if (ArriveT(null, 1000, 30, 0) != 1030) return "arrive empty";
            if (TLaunch(1430, 426) != 1004 || TIssue(1004, 20) != 984) return "launch / issue";
            if (IssueNow(900, 20, 426, 1430, 50, 3.5, 65)) return "issue too early";
            if (!IssueNow(990, 20, 426, 1430, 50, 3.5, 65)) return "issue on time";
            if (!IssueNow(1100, 20, 426, 1430, 50, 3.5, 65)) return "issue late still goes";
            if (IssueNow(990, 20, 426, 1430, 70, 3.5, 65) || IssueNow(990, 20, 426, 1430, 2, 3.5, 65)) return "issue out of the range window";
            if (SpreadS(new[] { 1430.0, 1436, 1431 }) != 6 || SpreadS(new[] { 5.0 }) != 0 || SpreadS(null) != 0) return "spread";
            var pp = new StrikeParams();
            if (DeltaPsi(350, 10) != 20 || DeltaPsi(-1, 77) != 90 || DeltaPsi(10, 350) != 20 || DeltaPsi(0, 180) != 180 || DeltaPsi(90, 0) != 90) return "delta psi";
            if (pp.TurnBaseS != 2.5 || pp.TurnRateDps != 11.5) return "turn parameters of the live calibration E1";
            if (Math.Abs(TurnLeadS(66, pp) - (pp.TurnBaseS + 66.0 / pp.TurnRateDps + pp.TurnLeadS)) > 1e-9 || Math.Abs(TurnLeadS(97, pp) - (pp.TurnBaseS + 97.0 / pp.TurnRateDps + pp.TurnLeadS)) > 1e-9 || Math.Abs(TurnLeadS(180, pp) - (pp.TurnBaseS + 180.0 / pp.TurnRateDps + pp.TurnLeadS)) > 1e-9) return "turn lead " + TurnLeadS(66, pp);
            // E1b: the turn model of the heavy carriers
            if (StrikeCalib.TurnRateDps("usn_a-6e", 450) != 0 || StrikeCalib.TurnRateDps(null, 450) != 0) return "agile types keep the E1 model";
            if (Math.Abs(StrikeCalib.TurnRateDps("wp_tu-22m2", 537) - 4.27) > 0.05 || Math.Abs(StrikeCalib.TurnRateDps("wp_tu-16k", 400) - 5.7) > 0.3) return "heavy turn rate " + StrikeCalib.TurnRateDps("wp_tu-22m2", 537);
            if (ArcExtraS(100, 0, 537, 4.27) > 0.01 || ArcExtraS(100, 90, 537, 0) != 0 || ArcExtraS(0, 90, 537, 4.27) != 0) return "arc: straight or no rate";
            if (Math.Abs(ArcExtraS(100, 90, 537, 4.27) - 7.8) > 0.3 || Math.Abs(ArcExtraS(100, -90, 537, 4.27) - 7.8) > 0.3) return "arc 90 " + ArcExtraS(100, 90, 537, 4.27);
            if (Math.Abs(ArcExtraS(100, 180, 537, 4.27) - 42.7) > 0.7 || Math.Abs(ArcExtraS(100, -180, 537, 4.27) - 42.7) > 0.7) return "arc 180 " + ArcExtraS(100, 180, 537, 4.27);
            if (ArcExtraS(100, 135, 537, 4.27) <= ArcExtraS(100, 90, 537, 4.27) || ArcExtraS(100, 180, 537, 4.27) <= ArcExtraS(100, 135, 537, 4.27)) return "arc grows with the turn";
            if (ArcExtraS(1, 90, 537, 4.27) < 20) return "a point inside the circle costs a turn first";
            if (Math.Abs(SignedDeltaDeg(350, 10) - 20) > 1e-9 || Math.Abs(SignedDeltaDeg(10, 350) + 20) > 1e-9 || SignedDeltaDeg(-1, 77) != 0) return "signed delta";
            if (Math.Abs(TurnLeadS(52, pp, 4.27) - (pp.TurnBaseS + 52 / 4.27 + pp.TurnLeadS)) > 1e-9 || TurnLeadS(52, pp, 0) != TurnLeadS(52, pp)) return "turn lead by the aircraft rate";
            if (TGo(1000, 15, 450) != 880 || TGo(1000, 15, 0) != 1000 - 15.0 / 400 * 3600) return "tgo";
            double on, off;
            EwWindow(1300, 1430, 15, out on, out off);
            if (on != 1300 || off != 1445) return "ew window";
            return null;
        }
    }
}
