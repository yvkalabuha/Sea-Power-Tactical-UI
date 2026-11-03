using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-100 / ADR-0060: button decision input of the air evasion.</summary>
    internal sealed class AeButtonIn { public bool IsAircraft, AttackRun, Already, HasMissile, IsLarge, NativeOk, HasThreat; }

    /// <summary>T-100: automatic (VB_AutoEvadeAir) decision input.</summary>
    internal sealed class AeAutoIn
    {
        public bool FlagOn, HasMissile, MissileAtUnit, IgnoredByGame, Muted, NativeActive, AttackRun, PlayerControl, Running;
        public double RangeNm, ClosingKn, NowS, TtiLimitS, MinNm, CooldownS;
        public double? SameMissileEndedS;      // end of the last run against this missile, null = new missile
    }

    /// <summary>T-100: pure core of the aircraft evasion (no Unity, no reflection).</summary>
    internal static class AirEvasionMath
    {
        public const double MinClosingKn = 0.5, ReplaceDeg = 10, LegNm = 10, QuietS = 30, ThreatMarginNm = 10;

        /// <summary>Seconds to impact; +Inf when the closing speed is below <see cref="MinClosingKn"/>.</summary>
        public static double Tti(double rangeNm, double closingKn)
        {
            if (closingKn < MinClosingKn) return double.PositiveInfinity;
            return Math.Max(0, rangeNm) / closingKn * 3600.0;
        }

        private static double N360(double a) { a %= 360; return a < 0 ? a + 360 : a; }

        private static double Rad(double d) { return d * Math.PI / 180.0; }

        /// <summary>T-348 / D1(b): false = a flat world uses the game metric (1 deg = 60 nm on both axes, stable 0.30 behaviour change); true = old physical cos in any world.</summary>
        internal const bool FlatWorldKeepsCos = false;

        /// <summary>East-west scale: cos of the mean latitude on the beta globe (or when keepCos), exactly 1.0 on the flat world.</summary>
        public static double LonK(double latA, double latB, bool globe, bool keepCos = FlatWorldKeepsCos)
        {
            return globe || keepCos ? Math.Cos(Rad((latA + latB) / 2)) : 1.0;
        }

        /// <summary>Bearing own -> target, degrees 0..360 (flat on stable, x LonK on the beta globe).</summary>
        public static double BearingDeg(double lat, double lon, double tLat, double tLon, bool globe, bool keepCos = FlatWorldKeepsCos)
        {
            double n = (tLat - lat) * 60.0, e = (tLon - lon) * 60.0 * LonK(lat, tLat, globe, keepCos);
            if (Math.Abs(n) < 1e-12 && Math.Abs(e) < 1e-12) return 0;
            return N360(Math.Atan2(e, n) * 180.0 / Math.PI);
        }

        public static double DistNm(double lat, double lon, double tLat, double tLon, bool globe, bool keepCos = FlatWorldKeepsCos)
        {
            double n = (tLat - lat) * 60.0, e = (tLon - lon) * 60.0 * LonK(lat, tLat, globe, keepCos);
            return Math.Sqrt(n * n + e * e);
        }

        /// <summary>Closing speed (kn): projection of the relative velocity on the line of sight, &gt;0 = closing.</summary>
        public static double ClosingKn(double ownLat, double ownLon, double ownCourseDeg, double ownKn,
                                       double mLat, double mLon, double mCourseDeg, double mKn, bool globe, bool keepCos = FlatWorldKeepsCos)
        {
            double n = (mLat - ownLat) * 60.0, e = (mLon - ownLon) * 60.0 * LonK(ownLat, mLat, globe, keepCos);
            double d = Math.Sqrt(n * n + e * e);
            if (d < 1e-9) return 0;
            double ln = n / d, le = e / d;
            double ovn = ownKn * Math.Cos(Rad(ownCourseDeg)), ove = ownKn * Math.Sin(Rad(ownCourseDeg));
            double mvn = mKn * Math.Cos(Rad(mCourseDeg)), mve = mKn * Math.Sin(Rad(mCourseDeg));
            double rn = mvn - ovn, re = mve - ove;
            return -(rn * ln + re * le);
        }

        /// <summary>Fallback without speeds: range change between two ticks (kn).</summary>
        public static double ClosingFromRanges(double prevNm, double nowNm, double dtS)
        {
            if (dtS <= 1e-9) return 0;
            return (prevNm - nowNm) / dtS * 3600.0;
        }

        /// <summary>ADR-0060 item 2: not_aircraft, attack_run, already, missile (native / combo_missile), threat (beam), no_threat.</summary>
        public static string ButtonMode(AeButtonIn x)
        {
            if (!x.IsAircraft) return "skip:not_aircraft";
            if (x.AttackRun) return "skip:attack_run";
            if (x.Already) return "skip:already";
            if (x.HasMissile) return x.IsLarge || !x.NativeOk ? "combo_missile" : "native";
            if (x.HasThreat) return "beam";
            return "skip:no_threat";
        }

        public static string AutoDecision(AeAutoIn x)
        {
            if (!x.FlagOn) return "skip:off";
            if (!x.HasMissile) return "skip:no_missile";
            if (!x.MissileAtUnit) return "skip:not_at_unit";
            if (x.IgnoredByGame) return "skip:ignored";
            if (x.Muted) return "skip:muted";
            if (x.NativeActive) return "skip:native";
            if (x.AttackRun) return "skip:attack_run";
            if (x.PlayerControl) return "skip:player_control";
            if (x.Running) return "skip:running";
            if (x.SameMissileEndedS.HasValue && x.NowS - x.SameMissileEndedS.Value < x.CooldownS) return "skip:cooldown";
            if (x.ClosingKn < MinClosingKn) return "skip:opening";
            double tti = Tti(x.RangeNm, x.ClosingKn);
            return tti <= x.TtiLimitS || x.RangeNm <= x.MinNm ? "start" : "wait";
        }

        public static double BeamCourse(double headingDeg, double bearingToThreatDeg) { return TacticsMath.BeamCourse(headingDeg, bearingToThreatDeg); }

        public static bool NeedReplace(double lastCourseDeg, double newCourseDeg) { return TacticsMath.Diff(lastCourseDeg, newCourseDeg) >= ReplaceDeg - 1e-9; }

        /// <summary>null while the beam run goes on; "upgrade" (a missile appeared), "threat_gone" (quiet for 30 s), "timeout".</summary>
        public static string BeamEnd(double nowS, double startedS, double lastThreatSeenS, double beamMaxS, bool missileNow)
        {
            if (missileNow) return "upgrade";
            if (nowS - lastThreatSeenS >= QuietS - 1e-9) return "threat_gone";
            if (nowS - startedS >= beamMaxS - 1e-9) return "timeout";
            return null;
        }

        private static bool Near(double a, double b, double eps) { return Math.Abs(a - b) <= eps; }

        public static string SelfTest()
        {
            if (!double.IsPositiveInfinity(Tti(10, 0))) return "air evasion: tti zero closing";
            if (!Near(Tti(10, 600), 60, 1e-6)) return "air evasion: tti 10 nm at 600 kn";
            if (!double.IsPositiveInfinity(Tti(10, -50))) return "air evasion: tti opening";
            if (!Near(ClosingKn(0, 0, 0, 100, 1, 0, 180, 500, false), 600, 0.5)) return "air evasion: closing head-on";
            return null;
        }
    }
}
