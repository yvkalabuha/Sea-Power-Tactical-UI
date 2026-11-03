using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// Pure helpers for the 0.9.0 tactics (T-064): geometry, guidance names, decision tables. No game access, so
    /// they are checked offline by SelfTest() (selfcheck prints "tactics selftest ok").
    /// </summary>
    internal static class TacticsMath
    {
        private const double Rad = Math.PI / 180.0;

        /// <summary>Distance (nm) from point P to segment A-B. Local flat approximation around P (fine below ~100 nm).</summary>
        public static double SegmentDistanceNm(double aLat, double aLon, double bLat, double bLon, double pLat, double pLon)
        {
            double k = Math.Cos(pLat * Rad);
            double ax = (aLon - pLon) * 60.0 * k, ay = (aLat - pLat) * 60.0;
            double bx = (bLon - pLon) * 60.0 * k, by = (bLat - pLat) * 60.0;
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, -(ax * dx + ay * dy) / len2));
            double cx = ax + t * dx, cy = ay + t * dy;
            return Math.Sqrt(cx * cx + cy * cy);
        }

        private static double N360(double a) { a %= 360; return a < 0 ? a + 360 : a; }

        /// <summary>Course +-90 deg to the target bearing, on the side that needs the smaller turn from the own heading.</summary>
        public static double BeamCourse(double ownHeadingDeg, double bearingToTargetDeg)
        {
            double c1 = N360(bearingToTargetDeg + 90), c2 = N360(bearingToTargetDeg - 90);
            return Diff(ownHeadingDeg, c1) <= Diff(ownHeadingDeg, c2) ? c1 : c2;
        }

        public static double AwayCourse(double bearingToThreatDeg) { return N360(bearingToThreatDeg + 180); }

        internal static double Diff(double a, double b) { double d = Math.Abs(N360(a) - N360(b)); return d > 180 ? 360 - d : d; }

        // AmmunitionParameters.GuidanceType order (game 0.8.6)
        private static readonly string[] Guidance = { "none", "ir", "sarh", "arh", "arm", "laser", "tv", "sonar_active", "sonar_passive", "wake", "sonar_ap" };

        public static string GuidanceName(int guidanceEnum)
        {
            return guidanceEnum >= 0 && guidanceEnum < Guidance.Length ? Guidance[guidanceEnum] : "none";
        }

        /// <summary>The launcher (or an illuminating sensor) must keep the target: the shooter may not break away.</summary>
        public static bool NeedsIllumination(string guidance, string midcourse)
        {
            if (guidance == "sarh" || guidance == "laser" || guidance == "tv") return true;
            return midcourse == "radio" || midcourse == "datalink" || midcourse == "wire";
        }

        /// <summary>Veto when a neutral is closer than radiusNm to the shooter-target segment (or to the target).</summary>
        public static bool ShouldVeto(double[] shooter, double[] target, IList<double[]> neutrals, double radiusNm, out int index, out double distNm)
        {
            index = -1; distNm = double.MaxValue;
            if (target == null || neutrals == null) return false;
            for (int i = 0; i < neutrals.Count; i++)
            {
                var n = neutrals[i];
                if (n == null) continue;
                double d = shooter == null
                    ? GameApi.DistanceNm(target[0], target[1], n[0], n[1])
                    : SegmentDistanceNm(shooter[0], shooter[1], target[0], target[1], n[0], n[1]);
                if (d < distNm) { distNm = d; index = i; }
            }
            return index >= 0 && distNm < radiusNm;
        }

        /// <summary>Nearest candidate within maxRangeNm that is not excluded; candidates are [lat, lon, key]. Returns index or -1.</summary>
        public static int PickNextTarget(double[] own, IList<double[]> candidates, IList<int> excluded, double maxRangeNm)
        {
            int best = -1; double bd = double.MaxValue;
            if (own == null || candidates == null) return -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null) continue;
                if (excluded != null && c.Length > 2 && excluded.Contains((int)c[2])) continue;
                double d = GameApi.DistanceNm(own[0], own[1], c[0], c[1]);
                if (d > maxRangeNm || d >= bd) continue;
                bd = d; best = i;
            }
            return best;
        }

        /// <summary>Auto torpedo evasion entry rule (flag, real threat that the player already knows about, state priority lower than evasion).</summary>
        public static bool ShouldStartEvasion(bool flag, bool hasThreat, bool playerKnows, int curPriority, int evasionPriority, bool inEvasion, double sinceLastS)
        {
            return flag && hasThreat && playerKnows && !inEvasion && curPriority > evasionPriority && sinceLastS >= 5.0;
        }

        /// <summary>"After launch" decision for one new missile; mode off|evade|retarget.</summary>
        public static string PostLaunchDecision(string mode, string guidance, string midcourse, bool targetIsAir, double rangeNm, bool roeHold, bool guidingOther)
        {
            if (mode != "evade" && mode != "retarget") return "none";
            if (!targetIsAir) return "none";
            if (NeedsIllumination(guidance, midcourse)) return "skip_guided";
            if (guidance != "ir" && guidance != "arh" && guidance != "arm") return "none";
            if (guidingOther) return "skip_guided";
            if (roeHold) return "skip_hold";
            if (rangeNm < 3.0) return "skip_close";
            return mode;
        }

        /// <summary>True when the game version is in the comma-separated verified list (spaces ignored).</summary>
        public static bool VersionKnown(string version, string csv)
        {
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(csv)) return false;
            foreach (var part in csv.Split(','))
                if (part.Trim().Length > 0 && string.Equals(part.Trim(), version.Trim(), StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Point at distance nm from a toward b (negative or beyond = extrapolate), flat lat/lon (cos(lat) on lon).</summary>
        internal static double[] AlongNm(double[] a, double[] b, double nm)
        {
            double k = Math.Cos(a[0] * Rad);
            double dx = (b[1] - a[1]) * 60.0 * k, dy = (b[0] - a[0]) * 60.0;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return new[] { b[0], b[1] };
            double f = nm / len;
            return new[] { a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f };
        }

        /// <summary>
        /// Corridor of the seeker: last min(L, seekerLegNm) nm before the target plus overshootNm past it. Points are [lat, lon].
        /// Returns { index, distToTargetNm, distToCorridorNm } of neutrals with distToCorridor &lt; radiusNm, sorted by distToTarget.
        /// </summary>
        public static List<double[]> NeutralsNearAttack(double[] shooter, double[] target, IList<double[]> neutrals,
            double radiusNm, double seekerLegNm, double overshootNm)
        {
            var res = new List<double[]>();
            if (shooter == null || target == null || neutrals == null) return res;
            double k = Math.Cos(shooter[0] * Rad);
            double dx = (target[1] - shooter[1]) * 60.0 * k, dy = (target[0] - shooter[0]) * 60.0;
            double len = Math.Sqrt(dx * dx + dy * dy);
            var from = AlongNm(shooter, target, len - Math.Min(len, seekerLegNm));
            var to = AlongNm(shooter, target, len + overshootNm);
            for (int i = 0; i < neutrals.Count; i++)
            {
                var n = neutrals[i];
                if (n == null) continue;
                double dc = SegmentDistanceNm(from[0], from[1], to[0], to[1], n[0], n[1]);
                if (dc >= radiusNm) continue;
                double dt = GameApi.DistanceNm(target[0], target[1], n[0], n[1]);
                res.Add(new[] { (double)i, dt, dc });
            }
            res.Sort((p, q) => p[1].CompareTo(q[1]));
            return res;
        }

        /// <summary>Index of the nearest candidate with no neutral near its attack corridor; none clear = nearest overall; -1 if empty. skipped = closer candidates rejected.</summary>
        public static int PickNearestClear(double[] shooter, IList<double[]> candidates, IList<double[]> neutrals,
            double radiusNm, double seekerLegNm, double overshootNm, List<int> skipped)
        {
            if (shooter == null || candidates == null) return -1;
            var order = new List<int>();
            for (int i = 0; i < candidates.Count; i++) if (candidates[i] != null) order.Add(i);
            if (order.Count == 0) return -1;
            order.Sort((a, b) => GameApi.DistanceNm(shooter[0], shooter[1], candidates[a][0], candidates[a][1])
                .CompareTo(GameApi.DistanceNm(shooter[0], shooter[1], candidates[b][0], candidates[b][1])));
            var rejected = new List<int>();
            foreach (int i in order)
            {
                if (NeutralsNearAttack(shooter, candidates[i], neutrals, radiusNm, seekerLegNm, overshootNm).Count == 0)
                {
                    if (skipped != null) skipped.AddRange(rejected);
                    return i;
                }
                rejected.Add(i);
            }
            return order[0];
        }

        // ---------- self test (table driven) ----------

        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }
        private static bool Near(double a, double b, double eps) { return Math.Abs(a - b) <= eps; }

        public static string SelfTest()
        {
            _fail = null;
            // segment distance: A(0,0) B(0,1deg lon) = 60 nm east; P 1 nm north of the middle / beyond the end / on the segment
            Eq("seg middle 1nm", Near(SegmentDistanceNm(0, 0, 0, 1, 1.0 / 60, 0.5), 1.0, 0.01));
            Eq("seg beyond end", Near(SegmentDistanceNm(0, 0, 0, 1, 0, 1.0 + 3.0 / 60), 3.0, 0.01));
            Eq("seg before start", Near(SegmentDistanceNm(0, 0, 0, 1, 0, -2.0 / 60), 2.0, 0.01));
            Eq("seg on segment", Near(SegmentDistanceNm(0, 0, 0, 1, 0, 0.3), 0.0, 0.001));
            Eq("seg degenerate", Near(SegmentDistanceNm(0, 0, 0, 0, 1.0 / 60, 0), 1.0, 0.01));
            // beam course: smaller turn
            Eq("beam hdg 0 tgt 0 -> 90", Near(BeamCourse(0, 0), 90, 1e-6) || Near(BeamCourse(0, 0), 270, 1e-6));
            Eq("beam hdg 80 tgt 0 -> 90", Near(BeamCourse(80, 0), 90, 1e-6));
            Eq("beam hdg 280 tgt 0 -> 270", Near(BeamCourse(280, 0), 270, 1e-6));
            Eq("beam wrap tgt 350 hdg 10 -> 80", Near(BeamCourse(10, 350), 80, 1e-6));
            Eq("beam tgt 10 hdg 355 -> 280", Near(BeamCourse(355, 10), 280, 1e-6));
            Eq("away 90 -> 270", Near(AwayCourse(90), 270, 1e-6));
            Eq("away 350 -> 170", Near(AwayCourse(350), 170, 1e-6));
            // guidance names / illumination
            Eq("guid 0", GuidanceName(0) == "none");
            Eq("guid ir", GuidanceName(1) == "ir");
            Eq("guid sarh", GuidanceName(2) == "sarh");
            Eq("guid arh", GuidanceName(3) == "arh");
            Eq("guid arm", GuidanceName(4) == "arm");
            Eq("guid laser tv", GuidanceName(5) == "laser" && GuidanceName(6) == "tv");
            Eq("guid sonar", GuidanceName(7) == "sonar_active" && GuidanceName(8) == "sonar_passive" && GuidanceName(10) == "sonar_ap");
            Eq("guid wake", GuidanceName(9) == "wake");
            Eq("guid out of range", GuidanceName(11) == "none" && GuidanceName(-1) == "none");
            Eq("illum sarh/laser/tv", NeedsIllumination("sarh", "none") && NeedsIllumination("laser", "none") && NeedsIllumination("tv", "none"));
            Eq("illum radio/datalink/wire", NeedsIllumination("arh", "radio") && NeedsIllumination("ir", "datalink") && NeedsIllumination("arh", "wire"));
            Eq("no illum ir/arh/arm", !NeedsIllumination("ir", "none") && !NeedsIllumination("arh", "none") && !NeedsIllumination("arm", "none"));
            // veto: shooter at (0,0), target 20 nm east (lon 1/3 deg)
            var sh = new[] { 0.0, 0.0 }; var tg = new[] { 0.0, 20.0 / 60 };
            int idx; double dist;
            Eq("veto mid 1.5nm", ShouldVeto(sh, tg, new List<double[]> { new[] { 1.5 / 60, 10.0 / 60 } }, 2.0, out idx, out dist) && idx == 0 && Near(dist, 1.5, 0.02));
            Eq("veto mid 2.5nm no", !ShouldVeto(sh, tg, new List<double[]> { new[] { 2.5 / 60, 10.0 / 60 } }, 2.0, out idx, out dist));
            Eq("veto at target 1.9", ShouldVeto(sh, tg, new List<double[]> { new[] { 1.9 / 60, 20.0 / 60 } }, 2.0, out idx, out dist));
            Eq("veto behind shooter 1nm but 1nm from segment start", ShouldVeto(sh, tg, new List<double[]> { new[] { 0.0, -1.0 / 60 } }, 0.5, out idx, out dist) == false);
            Eq("veto behind shooter far from segment no", !ShouldVeto(sh, tg, new List<double[]> { new[] { 0.0, -3.0 / 60 } }, 2.0, out idx, out dist));
            Eq("veto picks nearest", ShouldVeto(sh, tg, new List<double[]> { new[] { 1.8 / 60, 10.0 / 60 }, new[] { 0.5 / 60, 12.0 / 60 } }, 2.0, out idx, out dist) && idx == 1);
            Eq("veto no neutrals", !ShouldVeto(sh, tg, new List<double[]>(), 2.0, out idx, out dist));
            Eq("veto no shooter uses target", ShouldVeto(null, tg, new List<double[]> { new[] { 1.0 / 60, 20.0 / 60 } }, 2.0, out idx, out dist));
            // next target
            var own = new[] { 0.0, 0.0 };
            var cands = new List<double[]> { new[] { 0.0, 30.0 / 60, 1 }, new[] { 0.0, 10.0 / 60, 2 }, new[] { 0.0, 20.0 / 60, 3 } };
            Eq("pick nearest", PickNextTarget(own, cands, new List<int>(), 40) == 1);
            Eq("pick skips excluded", PickNextTarget(own, cands, new List<int> { 2 }, 40) == 2);
            Eq("pick out of range", PickNextTarget(own, cands, new List<int>(), 5) == -1);
            Eq("pick range limit", PickNextTarget(own, cands, new List<int> { 2, 3 }, 25) == -1 && PickNextTarget(own, cands, new List<int> { 2 }, 35) == 2);
            Eq("pick empty", PickNextTarget(own, new List<double[]>(), null, 40) == -1);
            // evasion entry
            Eq("evade yes", ShouldStartEvasion(true, true, true, 5, 2, false, 10));
            Eq("evade flag off", !ShouldStartEvasion(false, true, true, 5, 2, false, 10));
            Eq("evade no threat", !ShouldStartEvasion(true, false, true, 5, 2, false, 10));
            Eq("evade player unaware", !ShouldStartEvasion(true, true, false, 5, 2, false, 10));
            Eq("evade already", !ShouldStartEvasion(true, true, true, 2, 2, true, 10));
            Eq("evade priority not lower", !ShouldStartEvasion(true, true, true, 2, 2, false, 10));
            Eq("evade cooldown", !ShouldStartEvasion(true, true, true, 5, 2, false, 3));
            // post launch
            Eq("pl sarh evade", PostLaunchDecision("evade", "sarh", "none", true, 20, false, false) == "skip_guided");
            Eq("pl sarh retarget", PostLaunchDecision("retarget", "sarh", "none", true, 20, false, false) == "skip_guided");
            Eq("pl arh off", PostLaunchDecision("off", "arh", "none", true, 20, false, false) == "none");
            Eq("pl ir retarget", PostLaunchDecision("retarget", "ir", "none", true, 20, false, false) == "retarget");
            Eq("pl arh evade", PostLaunchDecision("evade", "arh", "none", true, 20, false, false) == "evade");
            Eq("pl arh datalink", PostLaunchDecision("evade", "arh", "datalink", true, 20, false, false) == "skip_guided");
            Eq("pl close", PostLaunchDecision("evade", "arh", "none", true, 2.9, false, false) == "skip_close");
            Eq("pl hold", PostLaunchDecision("evade", "arh", "none", true, 20, true, false) == "skip_hold");
            Eq("pl guiding other", PostLaunchDecision("evade", "arh", "none", true, 20, false, true) == "skip_guided");
            Eq("pl surface target", PostLaunchDecision("evade", "arh", "none", false, 20, false, false) == "none");
            Eq("pl sonar", PostLaunchDecision("evade", "sonar_active", "none", true, 20, false, false) == "none");
            // neutral guard (T-079): shooter (0,0), target 20 nm east
            var nsh = new[] { 0.0, 0.0 }; var ntg = new[] { 0.0, 20.0 / 60 };
            List<double[]> nr;
            nr = NeutralsNearAttack(nsh, ntg, new List<double[]> { new[] { 0.3 / 60, 20.0 / 60 } }, 2.0, 10, 2.0);
            Eq("ng at target 0.3", nr.Count == 1 && nr[0][0] == 0 && Near(nr[0][1], 0.3, 0.02));
            Eq("ng mid-leg 1.5 side", NeutralsNearAttack(nsh, ntg, new List<double[]> { new[] { 1.5 / 60, 10.0 / 60 } }, 2.0, 10, 2.0).Count == 1);
            Eq("ng mid-leg 1.5 side, leg 5 no", NeutralsNearAttack(nsh, ntg, new List<double[]> { new[] { 1.5 / 60, 10.0 / 60 } }, 2.0, 5, 2.0).Count == 0);
            Eq("ng near shooter 1nm no", NeutralsNearAttack(nsh, ntg, new List<double[]> { new[] { 1.0 / 60, 0.0 } }, 2.0, 10, 2.0).Count == 0);
            Eq("ng 1.5 behind target", NeutralsNearAttack(nsh, ntg, new List<double[]> { new[] { 0.0, 21.5 / 60 } }, 2.0, 10, 2.0).Count == 1);
            Eq("ng 4.5 behind target no", NeutralsNearAttack(nsh, ntg, new List<double[]> { new[] { 0.0, 24.5 / 60 } }, 2.0, 10, 2.0).Count == 0);
            Eq("ng short path from shooter", NeutralsNearAttack(nsh, new[] { 0.0, 4.0 / 60 }, new List<double[]> { new[] { 1.0 / 60, 0.0 } }, 2.0, 10, 2.0).Count == 1);
            Eq("ng empty", NeutralsNearAttack(nsh, ntg, new List<double[]>(), 2.0, 10, 2.0).Count == 0);
            nr = NeutralsNearAttack(nsh, ntg, new List<double[]> { new[] { 1.5 / 60, 20.0 / 60 }, new[] { 0.3 / 60, 20.0 / 60 } }, 2.0, 10, 2.0);
            Eq("ng sorted by dist to target", nr.Count == 2 && nr[0][0] == 1 && nr[1][0] == 0);
            var al0 = AlongNm(nsh, ntg, 0); var alL = AlongNm(nsh, ntg, 20); var alX = AlongNm(nsh, ntg, 22);
            Eq("along 0/L/L+2", Near(al0[1], 0, 1e-9) && Near(alL[1] * 60, 20, 1e-6) && Near(alX[1] * 60, 22, 1e-6) && Near(alX[0], 0, 1e-9));
            // nearest clear: targets 5/10/15 nm east, neutral near the nearest
            var cl = new List<double[]> { new[] { 0.0, 5.0 / 60 }, new[] { 10.0 / 60, 0.0 }, new[] { 0.0, -15.0 / 60 } };
            var sk = new List<int>();
            Eq("pick clear skips first", PickNearestClear(nsh, cl, new List<double[]> { new[] { 0.3 / 60, 5.0 / 60 } }, 2.0, 10, 2.0, sk) == 1 && sk.Count == 1 && sk[0] == 0);
            sk = new List<int>();
            Eq("pick clear all blocked", PickNearestClear(nsh, cl, new List<double[]> { new[] { 0.0, 6.0 / 60 }, new[] { 11.0 / 60, 0.0 }, new[] { 0.0, -16.0 / 60 } }, 2.0, 10, 2.0, sk) == 0 && sk.Count == 0);
            sk = new List<int>();
            Eq("pick clear none neutral", PickNearestClear(nsh, cl, new List<double[]>(), 2.0, 10, 2.0, sk) == 0 && sk.Count == 0);
            Eq("pick clear empty", PickNearestClear(nsh, new List<double[]>(), new List<double[]>(), 2.0, 10, 2.0, new List<int>()) == -1);
            // version
            Eq("ver exact", VersionKnown("0.8.6-261014", "0.8.6-261014"));
            Eq("ver spaces", VersionKnown("0.8.6-261014", " 0.8.5-261008 , 0.8.6-261014 "));
            Eq("ver other", !VersionKnown("0.8.7-261016", "0.8.6-261014"));
            Eq("ver empty list", !VersionKnown("0.8.6-261014", ""));
            Eq("ver empty version", !VersionKnown("", "0.8.6-261014"));
            Eq("ver prefix is not a match", !VersionKnown("0.8.6", "0.8.6-261014"));
            return _fail;
        }
    }
}
