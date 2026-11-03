using System;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-100 A1: AirEvasionMath cases.</summary>
    internal static class AirEvasionMathTests
    {
        private static string Req(bool ok, string msg) { return ok ? null : msg; }

        private static bool Near(double a, double b, double e) { return Math.Abs(a - b) <= e; }

        public static string Tti()
        {
            return Req(Near(AirEvasionMath.Tti(0, 600), 0, 1e-9), "tti range 0")
                ?? Req(Near(AirEvasionMath.Tti(10, 600), 60, 1e-6), "tti 10 nm / 600 kn = 60 s")
                ?? Req(double.IsPositiveInfinity(AirEvasionMath.Tti(10, 0)), "tti zero closing")
                ?? Req(double.IsPositiveInfinity(AirEvasionMath.Tti(10, -300)), "tti opening")
                ?? Req(double.IsPositiveInfinity(AirEvasionMath.Tti(10, 0.4)), "tti below MinClosingKn")
                ?? Req(Near(AirEvasionMath.Tti(10, 0.5), 72000, 1e-6), "tti at MinClosingKn");
        }

        public static string Closing()
        {
            double head = AirEvasionMath.ClosingKn(0, 0, 0, 100, 1, 0, 180, 500, false);
            double tail = AirEvasionMath.ClosingKn(0, 0, 0, 100, 1, 0, 0, 500, false);        // missile north of us, flies north faster: opening
            double chase = AirEvasionMath.ClosingKn(0, 0, 0, 500, 1, 0, 0, 100, false);       // we chase a slower missile: closing 400
            double beam = AirEvasionMath.ClosingKn(0, 0, 90, 300, 1, 0, 90, 300, false);      // same velocity: 0
            double abeam = AirEvasionMath.ClosingKn(0, 0, 0, 0, 0, 1.0 / 60, 0, 400, false);  // missile east of us flies north: LOS projection 0
            return Req(Near(head, 600, 0.5), "closing head-on " + head)
                ?? Req(Near(tail, -400, 0.5), "closing opening " + tail)
                ?? Req(Near(chase, 400, 0.5), "closing chase " + chase)
                ?? Req(Near(beam, 0, 0.5), "closing same velocity " + beam)
                ?? Req(Near(abeam, 0, 0.5), "closing abeam " + abeam)
                ?? Req(Near(AirEvasionMath.ClosingFromRanges(10, 9, 6), 600, 1e-6), "closing from ranges")
                ?? Req(AirEvasionMath.ClosingFromRanges(10, 9, 0) == 0, "closing from ranges dt 0");
        }

        // ---- T-348 (lat): world-aware geometry on 0/45/60/70 deg ----

        private static double GreatCircleNm(double la, double lo, double lb, double lc)
        {
            double r = Math.PI / 180.0, p1 = la * r, p2 = lb * r, dl = (lc - lo) * r;
            double c = Math.Sin(p1) * Math.Sin(p2) + Math.Cos(p1) * Math.Cos(p2) * Math.Cos(dl);
            return Math.Acos(Math.Max(-1, Math.Min(1, c))) * 180.0 / Math.PI * 60.0;
        }

        private static double TrueAzimuth(double la, double lo, double lb, double lc)
        {
            double r = Math.PI / 180.0, p1 = la * r, p2 = lb * r, dl = (lc - lo) * r;
            double y = Math.Sin(dl) * Math.Cos(p2), x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            double a = Math.Atan2(y, x) * 180.0 / Math.PI; return a < 0 ? a + 360 : a;
        }

        private static double OldDist(double lat, double lon, double tLat, double tLon)
        {
            double n = (tLat - lat) * 60.0, e = (tLon - lon) * 60.0 * Math.Cos((lat + tLat) / 2 * Math.PI / 180.0);
            return Math.Sqrt(n * n + e * e);
        }

        private static double OldBearing(double lat, double lon, double tLat, double tLon)
        {
            double n = (tLat - lat) * 60.0, e = (tLon - lon) * 60.0 * Math.Cos((lat + tLat) / 2 * Math.PI / 180.0);
            double a = Math.Atan2(e, n) * 180.0 / Math.PI % 360; return a < 0 ? a + 360 : a;
        }

        public static string Lat()
        {
            foreach (double phi in new[] { 0.0, 45, 60, 70 })
            foreach (double[] d in new[] { new[] { 0.1, 0.1 }, new[] { 0.0, 0.5 }, new[] { -0.3, 0.4 }, new[] { 0.6, -0.2 } })
            {
                double la = phi, lo = 10, lb = phi + d[0], lc = 10 + d[1];
                string t = " at " + phi + " d=" + d[0] + "," + d[1];
                // beta globe: true distance within 1.5 %, true azimuth within 0.5 deg (<= 80 nm)
                double dn = AirEvasionMath.DistNm(la, lo, lb, lc, true), gc = GreatCircleNm(la, lo, lb, lc);
                if (gc > 1e-6 && gc <= 80 && Math.Abs(dn / gc - 1) > 0.015) return "globe dist " + dn + " vs " + gc + t;
                double az = AirEvasionMath.BearingDeg(la, lo, lb, lc, true), taz = TrueAzimuth(la, lo, lb, lc);
                double da = Math.Abs(az - taz); if (da > 180) da = 360 - da;
                if (gc <= 80 && da > 0.5) return "globe bearing " + az + " vs " + taz + t;
                // old cos behaviour is bit-identical when globe or keepCos
                if (AirEvasionMath.DistNm(la, lo, lb, lc, true) != OldDist(la, lo, lb, lc)) return "globe != old dist" + t;
                if (AirEvasionMath.DistNm(la, lo, lb, lc, false, true) != OldDist(la, lo, lb, lc)) return "keepCos != old dist" + t;
                if (AirEvasionMath.BearingDeg(la, lo, lb, lc, false, true) != OldBearing(la, lo, lb, lc)) return "keepCos != old bearing" + t;
                // flat world (D1 b, the default): game metric 1 deg = 60 nm on both axes
                double fn = AirEvasionMath.DistNm(la, lo, lb, lc, false), want = 60.0 * Math.Sqrt(d[0] * d[0] + d[1] * d[1]);
                if (Math.Abs(fn - want) > 1e-9) return "flat dist " + fn + " vs " + want + t;
                double fb = AirEvasionMath.BearingDeg(la, lo, lb, lc, false), wb = Math.Atan2(d[1], d[0]) * 180.0 / Math.PI; if (wb < 0) wb += 360;
                if (Math.Abs(fb - wb) > 1e-9) return "flat bearing " + fb + " vs " + wb + t;
            }
            // documented D1(b) change: at 60 deg, dlat = dlon = 0.1 flat gives 45 deg / 8.485 nm, the old flat+cos gave 26.6 deg / 6.708 nm
            double nb = AirEvasionMath.BearingDeg(60, 10, 60.1, 10.1, false), nd = AirEvasionMath.DistNm(60, 10, 60.1, 10.1, false);
            double ob = AirEvasionMath.BearingDeg(60, 10, 60.1, 10.1, false, true), od = AirEvasionMath.DistNm(60, 10, 60.1, 10.1, false, true);
            return Req(Near(nb, 45, 1e-9) && Near(nd, 8.485, 1e-3), "D1(b) flat 60: " + nb + " / " + nd)
                ?? Req(Near(ob, 26.6, 0.1) && Near(od, 6.708, 2e-3) && Math.Abs(nb - ob) > 18, "old flat+cos differs: " + ob + " / " + od)
                ?? Req(AirEvasionMath.FlatWorldKeepsCos == false, "D1(b) switch is false")
                ?? Req(AirEvasionMath.LonK(60, 60, false) == 1.0 && Near(AirEvasionMath.LonK(60, 60, true), 0.5, 1e-12), "LonK");
        }

        public static string Button()
        {
            Func<Action<AeButtonIn>, string> m = a => { var x = new AeButtonIn { IsAircraft = true, NativeOk = true }; a(x); return AirEvasionMath.ButtonMode(x); };
            return Req(m(x => x.IsAircraft = false) == "skip:not_aircraft", "not_aircraft")
                ?? Req(m(x => { x.AttackRun = true; x.HasMissile = true; }) == "skip:attack_run", "attack_run")
                ?? Req(m(x => { x.Already = true; x.HasMissile = true; }) == "skip:already", "already")
                ?? Req(m(x => x.HasMissile = true) == "native", "native")
                ?? Req(m(x => { x.HasMissile = true; x.IsLarge = true; }) == "combo_missile", "large -> combo")
                ?? Req(m(x => { x.HasMissile = true; x.NativeOk = false; }) == "combo_missile", "no native -> combo")
                ?? Req(m(x => x.HasThreat = true) == "beam", "beam")
                ?? Req(m(x => { }) == "skip:no_threat", "no_threat")
                ?? Req(m(x => { x.HasMissile = true; x.HasThreat = true; }) == "native", "missile wins over threat");
        }

        private static AeAutoIn Auto(Action<AeAutoIn> a)
        {
            var x = new AeAutoIn { FlagOn = true, HasMissile = true, MissileAtUnit = true, RangeNm = 5, ClosingKn = 900, NowS = 100, TtiLimitS = 20, MinNm = 3, CooldownS = 10 };
            a(x);
            return x;
        }

        public static string AutoOf(Action<AeAutoIn> a) { return AirEvasionMath.AutoDecision(Auto(a)); }

        public static string Auto()
        {
            // 5 nm at 900 kn = 20 s; 5.01 nm = 20.04 s
            return Req(AutoOf(x => { }) == "start", "tti 20.0 starts: " + AutoOf(x => { }))
                ?? Req(AutoOf(x => x.RangeNm = 5.01) == "wait", "tti 20.04 waits")
                ?? Req(AutoOf(x => { x.RangeNm = 3; x.ClosingKn = 5; }) == "start", "MinNm starts")
                ?? Req(AutoOf(x => { x.RangeNm = 3.1; x.ClosingKn = 5; }) == "wait", "above MinNm waits")
                ?? Req(AutoOf(x => x.FlagOn = false) == "skip:off", "off")
                ?? Req(AutoOf(x => x.HasMissile = false) == "skip:no_missile", "no_missile")
                ?? Req(AutoOf(x => x.MissileAtUnit = false) == "skip:not_at_unit", "not_at_unit")
                ?? Req(AutoOf(x => x.IgnoredByGame = true) == "skip:ignored", "ignored")
                ?? Req(AutoOf(x => x.Muted = true) == "skip:muted", "muted")
                ?? Req(AutoOf(x => x.NativeActive = true) == "skip:native", "native")
                ?? Req(AutoOf(x => x.AttackRun = true) == "skip:attack_run", "attack_run")
                ?? Req(AutoOf(x => x.PlayerControl = true) == "skip:player_control", "player_control")
                ?? Req(AutoOf(x => x.Running = true) == "skip:running", "running")
                ?? Req(AutoOf(x => x.ClosingKn = 0.4) == "skip:opening", "opening");
        }

        public static string Cooldown()
        {
            return Req(AutoOf(x => x.SameMissileEndedS = 90.1) == "skip:cooldown", "9.9 s after: cooldown")
                ?? Req(AutoOf(x => x.SameMissileEndedS = 90) == "start", "10 s after: start")
                ?? Req(AutoOf(x => x.SameMissileEndedS = null) == "start", "new missile: start at once");
        }

        public static string Beam()
        {
            return Req(Near(AirEvasionMath.BeamCourse(80, 0), 90, 1e-6), "beam hdg 80 -> 90")
                ?? Req(Near(AirEvasionMath.BeamCourse(280, 0), 270, 1e-6), "beam hdg 280 -> 270")
                ?? Req(Near(AirEvasionMath.BeamCourse(10, 350), 80, 1e-6), "beam wrap 350/10")
                ?? Req(Near(AirEvasionMath.BeamCourse(355, 10), 280, 1e-6), "beam 10/355")
                ?? Req(AirEvasionMath.BeamCourse(0, 0) >= 0 && AirEvasionMath.BeamCourse(0, 0) < 360, "beam range 0..360")
                ?? Req(Near(AirEvasionMath.BearingDeg(0, 0, 1, 0, false), 0, 1e-6) && Near(AirEvasionMath.BearingDeg(0, 0, 0, 1, false), 90, 1e-6), "bearing");
        }

        public static string Replace()
        {
            return Req(!AirEvasionMath.NeedReplace(100, 109.9), "9.9 deg no")
                ?? Req(AirEvasionMath.NeedReplace(100, 110), "10 deg yes")
                ?? Req(AirEvasionMath.NeedReplace(355, 5), "wrap 10 deg yes")
                ?? Req(!AirEvasionMath.NeedReplace(355, 3), "wrap 8 deg no");
        }

        public static string BeamEnd()
        {
            return Req(AirEvasionMath.BeamEnd(100, 50, 99, 180, true) == "upgrade", "upgrade")
                ?? Req(AirEvasionMath.BeamEnd(100, 50, 70, 180, false) == "threat_gone", "threat_gone at 30 s")
                ?? Req(AirEvasionMath.BeamEnd(100, 50, 70.5, 180, false) == null, "29.5 s quiet goes on")
                ?? Req(AirEvasionMath.BeamEnd(230, 50, 229, 180, false) == "timeout", "timeout at 180 s")
                ?? Req(AirEvasionMath.BeamEnd(229, 50, 229, 180, false) == null, "179 s goes on");
        }
    }
}
