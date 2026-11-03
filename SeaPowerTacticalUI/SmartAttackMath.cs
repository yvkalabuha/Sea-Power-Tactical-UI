using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace VoiceBotBridge
{
    // T-102 / ADR-0038: pure maths of the sub "smart attack" (wire-guided torpedo with a lead-off leg, layer, final dash).
    // No Unity, no reflection into the game. Units: miles, knots, feet, seconds, degrees (course from north, clockwise).
    // Plane: the Unity plane in miles (X east, Y north) - the torpedo moves in exactly that space.

    internal struct SaVec
    {
        public double X, Y;
        public SaVec(double x, double y) { X = x; Y = y; }
        public static SaVec operator +(SaVec a, SaVec b) { return new SaVec(a.X + b.X, a.Y + b.Y); }
        public static SaVec operator -(SaVec a, SaVec b) { return new SaVec(a.X - b.X, a.Y - b.Y); }
        public static SaVec operator *(SaVec a, double k) { return new SaVec(a.X * k, a.Y * k); }
        public double Len { get { return Math.Sqrt(X * X + Y * Y); } }
        public static double Dist(SaVec a, SaVec b) { return (a - b).Len; }

        /// <summary>0..360, 0 = north (+Y), clockwise. A zero vector gives 0.</summary>
        public static double Bearing(SaVec from, SaVec to)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12) return 0;
            double b = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            if (b < 0) b += 360.0;
            return b >= 360.0 ? b - 360.0 : b;
        }

        public static SaVec FromBearing(double deg, double len)
        {
            double r = deg * Math.PI / 180.0;
            return new SaVec(Math.Sin(r) * len, Math.Cos(r) * len);
        }
    }

    /// <summary>Ammunition parameters read from the game (AmmunitionParameters) - NOT a list in code (ADR-0038 9a).</summary>
    internal sealed class WireAmmo
    {
        public string File;
        public int Count;
        public bool WireGuided, WireControlsDepth, Asw, Asuw;            // Asw/Asuw: _targetType | _secondaryTargetType
        public double[] SpeedsKn, RangesNm;                              // ascending speeds, full-tank range at each (miles)
        public double WireBreakKn = 20, MaxSpeedOnWireKn = double.PositiveInfinity, SeafloorClearanceFt,
                      TerminalKn, SeekerRangeNm, ActivateFraction, SeekerFovDeg, MaxDepthFt;
        public double ActivateRadiusNm { get { return SeekerRangeNm * ActivateFraction; } }

        private static string StripComment(string line)
        {
            int i = line.IndexOf("//", StringComparison.Ordinal);
            if (i >= 0) line = line.Substring(0, i);
            return line.Trim();
        }

        private static double Num(string s, double def)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static double[] List(string s)
        {
            var r = new List<double>();
            foreach (string part in s.Split(','))
            {
                double v;
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) r.Add(v);
            }
            return r.ToArray();
        }

        private static bool Bool(string s) { return string.Equals(s.Trim(), "true", StringComparison.OrdinalIgnoreCase); }

        /// <summary>Test helper: ini lines ("SpeedSettings=28,40,55,65 // comment"); '#' lines and '[...]' headers are ignored.</summary>
        public static WireAmmo FromIni(string file, string iniText)
        {
            var a = new WireAmmo { File = file, SpeedsKn = new double[0], RangesNm = new double[0] };
            double active = 0, passive = 0;
            foreach (string raw in (iniText ?? "").Split('\n'))
            {
                string line = StripComment(raw.TrimEnd('\r'));
                if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "MidCourseCorrection": a.WireGuided = (int)Num(v, 0) == 2; break;
                    case "WireControlsDepth": a.WireControlsDepth = Bool(v); break;
                    case "TargetType":
                    case "SecondaryTargetType":
                        if (string.Equals(v, "ASW", StringComparison.OrdinalIgnoreCase)) a.Asw = true;
                        if (string.Equals(v, "ASuW", StringComparison.OrdinalIgnoreCase)) a.Asuw = true;
                        break;
                    case "SpeedSettings": a.SpeedsKn = List(v); break;
                    case "SpeedSettingRanges": a.RangesNm = List(v); break;
                    case "WireBreakSpeed": a.WireBreakKn = Num(v, 20); break;
                    case "MaxSpeedOnWire": a.MaxSpeedOnWireKn = Num(v, double.PositiveInfinity); break;
                    case "WireMinSeafloorClearance": a.SeafloorClearanceFt = Num(v, 0); break;
                    case "TerminalVelocity": a.TerminalKn = Num(v, 0); break;
                    case "SeekerActiveRange": active = Num(v, 0); break;
                    case "SeekerPassiveRange": passive = Num(v, 0); break;
                    case "FractionOfRangeToActivateSeeker": a.ActivateFraction = Num(v, 0); break;
                    case "SeekerFOV": a.SeekerFovDeg = Num(v, 0); break;
                    case "MaxDepth": a.MaxDepthFt = Num(v, 0); break;
                }
            }
            a.SeekerRangeNm = Math.Max(active, passive);
            return a;
        }
    }

    /// <summary>[SmartAttack] config, ADR-0038 section 10 plus the values from sections 5-7.</summary>
    internal sealed class SmartParams
    {
        public double OffsetDeg = 60, MinOffsetDeg = 45, LegNm = 3, MinLegNm = 1, FuelReservePct = 15, RangeMargin = 1.1,
            CourseDeltaDeg = 15, CourseWindowS = 30, CourseSigmaDeg = 3, CourseBaseS = 120,
            SpeedDeltaKn = 3, SpeedDeltaFrac = 0.25, SpeedWindowS = 45, DepthDeltaFt = 100, DepthWindowS = 60,
            ConeDeg = 35, LayerMarginFt = 30, SubTargetMinDepthFt = 30, OwnDepthTolFt = 20, BottomMarginFt = 20,
            PositionTimeoutS = 180, LaunchTimeoutS = 90, TrackLostS = 60, NeutralConfirmTimeoutS = 30,
            NeutralRunTimeoutS = 20, PickTargetTimeoutS = 15, NeutralRecheckS = 5,
            CarrotFactor = 0.5, CarrotMaxNm = 3, CarrotSeekerMarginNm = 0.5, ShiftMaxFactor = 1.5,
            MaxTurnPerTickDeg = 10, OffsetUnstableDeg = 10, VetoRadiusNm = 2.0, SeekerFovMarginDeg = 5,
            FinalShipDepthFt = 6, AvoidLegMinNm = 1, AvoidLegMaxNm = 3, AvoidLegStepNm = 0.5,
            HitNearNm = 0.2;   // no hit counter: a torpedo that vanished while homing within this range of the target is a hit
        public int OffsetUnstableTicks = 3;
        public string RestoreOnLoad = "auto";   // off|ask|auto
    }

    /// <summary>One sample of the PLAYER's track (never the true object).</summary>
    internal struct TrackSample { public double T; public SaVec Pos; public SaVec VelKn; public double? DepthFt; }

    internal sealed class NeutralTrack { public int Id; public string Cls, Side /*neutral|unknown*/, Domain /*Surface|Subsurface*/; public SaVec Pos; }
    internal sealed class NeutralHitSa { public int Id; public string Cls, Side, Domain; public double DistToPathNm, AlongNm; }

    internal sealed class LegPlan { public double LegNm; public bool Skipped /*leg_skipped*/, OutOfRange; public double FuelAfterLeg; }

    internal sealed class DepthPlan
    {
        public int? OwnPreset;               // 3 above the layer, 4 below, null = do not change
        public bool NoDive;
        public double? TorpDepthFt;          // null = do not order
        public bool UseOwnDepth;             // no layer, sub target: torpedo goes at the carrier's depth (the caller knows it)
        public List<string> Warnings = new List<string>();
        public string Error;                 // no_wire_depth
    }

    internal sealed class SidePlan { public int Side; public double PhiDeg; public LegPlan Leg; public List<SaVec> Path; public List<NeutralHitSa> Hits; }
    internal sealed class Notice { public string Kind /*course|speed|depth*/; public double Delta, WindowS; }

    internal static class SmartAttackMath
    {
        public const double UnityPerNm = 27.559496, FtPerUnity = 220.47266;

        // ---- small helpers ----------------------------------------------------------------------------------------

        private static double Norm180(double d)
        {
            d %= 360.0;
            if (d > 180.0) d -= 360.0;
            if (d <= -180.0) d += 360.0;
            return d;
        }

        private static double Norm360(double d)
        {
            d %= 360.0;
            return d < 0 ? d + 360.0 : d;
        }

        private static double Dot(SaVec a, SaVec b) { return a.X * b.X + a.Y * b.Y; }

        private static double Median(List<double> v)
        {
            var s = new List<double>(v);
            s.Sort();
            int n = s.Count;
            return n == 0 ? 0 : (n % 2 == 1 ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]));
        }

        private static SaVec Unit(SaVec v, SaVec fallback)
        {
            double l = v.Len;
            return l < 1e-9 ? fallback : v * (1.0 / l);
        }

        // ---- ammunition -------------------------------------------------------------------------------------------

        public static string ValidateAmmo(WireAmmo a)
        {
            const string P = "ammo_params_invalid:";
            if (a == null) return P + "null";
            if (!a.WireGuided) return P + "not_wire";
            if (a.SpeedsKn == null || a.SpeedsKn.Length == 0) return P + "no_speeds";
            if (a.RangesNm == null || a.RangesNm.Length != a.SpeedsKn.Length) return P + "length_mismatch";
            for (int i = 0; i < a.SpeedsKn.Length; i++)
            {
                if (!(a.RangesNm[i] > 0)) return P + "range";
                if (!(a.SpeedsKn[i] > 0)) return P + "speed";
                if (i > 0 && a.SpeedsKn[i] <= a.SpeedsKn[i - 1]) return P + "speeds_order";
            }
            if (!(a.SeekerRangeNm > 0)) return P + "seeker_range";
            if (!(a.ActivateFraction > 0 && a.ActivateFraction <= 1.0)) return P + "activate_fraction";
            if (!(a.SeekerFovDeg > 0)) return P + "seeker_fov";
            return null;
        }

        /// <summary>As AmmunitionParameters.TorpedoRangeAtSpeed: linear interpolation, the ends are clamped.</summary>
        public static double RangeAtNm(WireAmmo a, double kn)
        {
            double[] s = a.SpeedsKn, r = a.RangesNm;
            if (kn <= s[0]) return r[0];
            for (int i = 1; i < s.Length; i++)
            {
                if (kn <= s[i])
                {
                    double t = (kn - s[i - 1]) / (s[i] - s[i - 1]);
                    return r[i - 1] + (r[i] - r[i - 1]) * t;
                }
            }
            return r[r.Length - 1];
        }

        public static double ClampOffset(double deg, SmartParams p) { return deg < p.MinOffsetDeg ? p.MinOffsetDeg : deg; }

        // ---- range ------------------------------------------------------------------------------------------------

        /// <summary>P(L): path to the intercept after a leg of L at angle phi (research section 6); a moving target adds its own run.</summary>
        public static double PathAfterLeg(double dNm, double legNm, double phiDeg, double targetKn, double torpKn)
        {
            double phi = phiDeg * Math.PI / 180.0;
            double ax = dNm - legNm * Math.Cos(phi), ay = legNm * Math.Sin(phi);
            double geom = Math.Sqrt(ax * ax + ay * ay);
            return geom + (torpKn > 0 ? targetKn * geom / torpKn : 0);
        }

        private const int MaxLoopSteps = 10000;

        /// <summary>Largest L &lt;= LegNm so that stage 0 still reaches: (1 - L/R0 - reserve) * R0 &gt;= margin * P(L).</summary>
        public static LegPlan PlanLeg(WireAmmo a, double dNm, double phiDeg, double targetKn, SmartParams p)
        {
            double r0 = a.RangesNm[0], v0 = a.SpeedsKn[0], res = p.FuelReservePct / 100.0;
            // T-154 E4: bad parameters must end in geometry_error (Start catches), never in a loop that does not end
            if (double.IsNaN(p.LegNm) || double.IsInfinity(p.LegNm) || p.LegNm > 1000) throw new InvalidOperationException("bad LegNm " + p.LegNm);
            int guard = 0;
            for (double l = p.LegNm; l >= -1e-9; l -= 0.05)
            {
                if (++guard > MaxLoopSteps) throw new InvalidOperationException("leg search did not end");
                double ll = l < 0 ? 0 : l;
                double f = 1.0 - ll / r0;
                if ((f - res) * r0 >= p.RangeMargin * PathAfterLeg(dNm, ll, phiDeg, targetKn, v0))
                    return new LegPlan { LegNm = Math.Round(ll, 3), Skipped = ll < p.MinLegNm, FuelAfterLeg = f };
            }
            return new LegPlan { LegNm = 0, Skipped = true, OutOfRange = true, FuelAfterLeg = 1.0 };
        }

        /// <summary>Highest stage (not above MaxSpeedOnWire) with (F - reserve) * R(v) &gt;= margin * path; none: stage 0 and fuelShort.</summary>
        public static int FinalStage(WireAmmo a, double fuelFrac, double pathNm, SmartParams p, out bool fuelShort)
        {
            double res = p.FuelReservePct / 100.0;
            fuelShort = false;
            for (int i = a.SpeedsKn.Length - 1; i >= 0; i--)
            {
                if (a.SpeedsKn[i] > a.MaxSpeedOnWireKn && i > 0) continue;
                if ((fuelFrac - res) * a.RangesNm[i] >= p.RangeMargin * pathNm) return i;
            }
            fuelShort = true;
            return 0;
        }

        public static double FuelFraction(double rangeLeftNm, double rangeAtCurrentNm)
        {
            if (!(rangeAtCurrentNm > 0)) return 0;
            double f = rangeLeftNm / rangeAtCurrentNm;
            return f < 0 ? 0 : (f > 1 ? 1 : f);
        }

        // ---- guidance ---------------------------------------------------------------------------------------------

        /// <summary>Port of Utils.CalculateInterceptPosition in 2D, with the lead angle clamped to FOV/2.5 as the game does.</summary>
        public static SaVec Intercept(SaVec shooter, double shooterKn, SaVec tgt, SaVec tgtVelKn, double seekerFovDeg)
        {
            SaVec d = tgt - shooter;
            double a = Dot(tgtVelKn, tgtVelKn) - shooterKn * shooterKn, b = 2.0 * Dot(d, tgtVelKn), c = Dot(d, d);
            double t;
            if (Math.Abs(a) < 1e-9)
            {
                if (Math.Abs(b) < 1e-9) return tgt;
                t = -c / b;
            }
            else
            {
                double disc = b * b - 4 * a * c;
                if (disc < 0) return tgt;
                double sq = Math.Sqrt(disc);
                double t1 = (-b - sq) / (2 * a), t2 = (-b + sq) / (2 * a);
                double lo = Math.Min(t1, t2), hi = Math.Max(t1, t2);
                t = lo > 0 ? lo : hi;
            }
            if (!(t > 0) || double.IsInfinity(t)) return tgt;
            SaVec ip = tgt + tgtVelKn * t;
            double limit = seekerFovDeg / 2.5;
            double los = SaVec.Bearing(shooter, tgt), lead = SaVec.Bearing(shooter, ip);
            double diff = Norm180(lead - los);
            if (Math.Abs(diff) > limit)
                ip = shooter + SaVec.FromBearing(los + Math.Sign(diff) * limit, SaVec.Dist(shooter, ip));
            return ip;
        }

        /// <summary>A = clamp(0.5*D, R_activate + 0.5, 3); the lower bound wins if the two disagree.</summary>
        public static double CarrotDistance(double dNm, WireAmmo a, SmartParams p)
        {
            double lo = a.ActivateRadiusNm + p.CarrotSeekerMarginNm;
            return Math.Max(lo, Math.Min(p.CarrotMaxNm, p.CarrotFactor * dNm));
        }

        /// <summary>Carrot point ahead of the torpedo along bearing(torp -> target) + side*phi; the direction turns at most MaxTurnPerTickDeg per tick.</summary>
        public static SaVec CarrotPoint(SaVec torp, SaVec target, int side, double phiDeg, double aNm, double? prevDirDeg, SmartParams p, out double dirDeg)
        {
            double want = Norm360(SaVec.Bearing(torp, target) + (side < 0 ? -1 : 1) * phiDeg);
            if (prevDirDeg.HasValue)
            {
                double delta = Norm180(want - prevDirDeg.Value);
                double lim = p.MaxTurnPerTickDeg;
                if (delta > lim) delta = lim;
                if (delta < -lim) delta = -lim;
                want = Norm360(prevDirDeg.Value + delta);
            }
            dirDeg = want;
            return torp + SaVec.FromBearing(want, aNm);
        }

        /// <summary>Shift (relative to the track) such that Intercept(torp, torpKn, trackPos + shift, trackVel) is about the carrot; |shift| &lt;= 1.5*D.</summary>
        public static SaVec ShiftFor(SaVec carrot, SaVec trackPos, SaVec trackVelKn, SaVec torp, double torpKn, double dNm, SmartParams p)
        {
            double t = torpKn > 0 ? SaVec.Dist(carrot, torp) / torpKn : 0;     // hours to the carrot at torpedo speed
            SaVec x = carrot - trackVelKn * t;                                   // the aim point whose intercept is the carrot
            SaVec shift = x - trackPos;
            double max = p.ShiftMaxFactor * dNm;
            double len = shift.Len;
            if (len > max && len > 1e-9) shift = shift * (max / len);
            return shift;
        }

        public static bool InCone(double torpCourseDeg, SaVec torp, SaVec target, double coneDeg)
        {
            return Math.Abs(Norm180(SaVec.Bearing(torp, target) - torpCourseDeg)) < coneDeg;
        }

        // ---- "target noticed the torpedo" -------------------------------------------------------------------------

        private static double SpeedOf(TrackSample s) { return s.VelKn.Len; }

        private static double CourseOf(TrackSample s) { return SaVec.Bearing(new SaVec(0, 0), s.VelKn); }

        public static Notice Noticed(IList<TrackSample> hist, double launchT, double now, bool targetIsSub, SmartParams p)
        {
            if (hist == null || hist.Count < 8) return null;
            Notice n = NoticeCourse(hist, launchT, now, p);
            if (n != null) return n;
            n = NoticeSpeed(hist, launchT, now, p);
            if (n != null) return n;
            return targetIsSub ? NoticeDepth(hist, launchT, now, p) : null;
        }

        private static Notice NoticeCourse(IList<TrackSample> hist, double launchT, double now, SmartParams p)
        {
            double winStart = Math.Max(now - p.CourseWindowS, launchT);
            var baseC = new List<double>();
            double c0 = 0;
            bool have0 = false;
            foreach (var s in hist)
            {
                if (s.T < winStart - p.CourseBaseS || s.T >= winStart || SpeedOf(s) < 1.0) continue;
                double c = CourseOf(s);
                if (!have0) { c0 = c; have0 = true; }
                baseC.Add(Norm180(c - c0));
            }
            if (baseC.Count < 8) return null;
            double mean = baseC.Average();
            double var = baseC.Sum(x => (x - mean) * (x - mean)) / baseC.Count;
            if (Math.Sqrt(var) >= p.CourseSigmaDeg) return null;
            double med = c0 + Median(baseC);
            foreach (var s in hist)
            {
                if (s.T < winStart || s.T > now || s.T < launchT || SpeedOf(s) < 1.0) continue;
                double dev = Norm180(CourseOf(s) - med);
                if (Math.Abs(dev) >= p.CourseDeltaDeg) return new Notice { Kind = "course", Delta = dev, WindowS = p.CourseWindowS };
            }
            return null;
        }

        private static Notice NoticeSpeed(IList<TrackSample> hist, double launchT, double now, SmartParams p)
        {
            double winStart = Math.Max(now - p.SpeedWindowS, launchT);
            var baseV = new List<double>();
            foreach (var s in hist) if (s.T >= winStart - 60 && s.T < winStart) baseV.Add(SpeedOf(s));
            if (baseV.Count < 5) return null;
            double b = Median(baseV);
            double thr = Math.Max(p.SpeedDeltaKn, p.SpeedDeltaFrac * b);
            foreach (var s in hist)
            {
                if (s.T < winStart || s.T > now || s.T < launchT) continue;
                double dv = SpeedOf(s) - b;
                if (Math.Abs(dv) >= thr) return new Notice { Kind = "speed", Delta = dv, WindowS = p.SpeedWindowS };
            }
            return null;
        }

        private static Notice NoticeDepth(IList<TrackSample> hist, double launchT, double now, SmartParams p)
        {
            double winStart = Math.Max(now - p.DepthWindowS, launchT);
            var baseD = new List<double>();
            foreach (var s in hist) if (s.DepthFt.HasValue && s.T >= winStart - 60 && s.T < winStart) baseD.Add(s.DepthFt.Value);
            if (baseD.Count < 5) return null;
            double b = Median(baseD);
            foreach (var s in hist)
            {
                if (!s.DepthFt.HasValue || s.T < winStart || s.T > now || s.T < launchT) continue;
                double dd = s.DepthFt.Value - b;
                if (Math.Abs(dd) >= p.DepthDeltaFt) return new Notice { Kind = "depth", Delta = dd, WindowS = p.DepthWindowS };
            }
            return null;
        }

        // ---- layer and depths -------------------------------------------------------------------------------------

        public static double MaxOwnDepthFt(double bottomFt, WireAmmo a, SmartParams p)
        {
            return bottomFt - (a.SeafloorClearanceFt + p.BottomMarginFt);
        }

        public static double ClampTorpDepth(double ft, double minRunFt, double maxFt, bool targetIsSub, SmartParams p)
        {
            double lo = Math.Max(minRunFt, targetIsSub ? p.SubTargetMinDepthFt : 0);
            double hi = maxFt > 0 ? maxFt : double.MaxValue;
            return Math.Min(hi, Math.Max(lo, ft));
        }

        /// <summary>ADR-0038 sections 4 and 9a. Only player-side data: own layer (own coordinates), the track depth estimate.</summary>
        public static DepthPlan PlanDepths(bool targetIsSub, double? ownLayerFt, bool sameTile, double? targetDepthFt,
                                           double bottomFt, WireAmmo a, double torpMinRunFt, int? ownPresetNow, SmartParams p)
        {
            var r = new DepthPlan();
            double maxOwn = MaxOwnDepthFt(bottomFt, a, p);
            if (!a.WireControlsDepth && targetIsSub) { r.Error = "no_wire_depth"; return r; }
            if (!a.WireControlsDepth) r.Warnings.Add("no_wire_depth");

            bool haveLayer = ownLayerFt.HasValue;
            double layer = haveLayer ? ownLayerFt.Value : 0;
            bool canDive = haveLayer && layer < maxOwn;
            if (haveLayer && !canDive) { r.NoDive = true; r.Warnings.Add("layer_too_deep"); }

            if (!haveLayer)
            {
                r.NoDive = true;
                r.Warnings.Add("no_layer");
                if (targetIsSub) r.UseOwnDepth = true;
                return r;
            }

            double? torp;
            int? preset = null;
            if (!targetIsSub)
            {
                if (canDive) preset = 4;
                torp = layer + p.LayerMarginFt;
            }
            else if (!sameTile || !targetDepthFt.HasValue)
            {
                r.Warnings.Add("depth_unknown");
                torp = layer + p.LayerMarginFt;
            }
            else if (targetDepthFt.Value < layer)
            {
                // the target is above the layer: we and the torpedo go below it
                if (canDive) preset = 4;
                torp = layer + p.LayerMarginFt;
            }
            else
            {
                // the target is below the layer: we and the torpedo stay above it
                preset = 3;
                torp = layer - p.LayerMarginFt;
            }
            if (preset.HasValue && ownPresetNow.HasValue && ownPresetNow.Value == preset.Value) preset = null;
            r.OwnPreset = preset;
            if (a.WireControlsDepth) r.TorpDepthFt = ClampTorpDepth(torp.Value, torpMinRunFt, a.MaxDepthFt, targetIsSub, p);
            return r;
        }

        // ---- corridor ---------------------------------------------------------------------------------------------

        /// <summary>Polyline launch -> end of the leg -> intercept, plus an overshoot of max(veto, R_seeker) past the intercept.</summary>
        public static List<SaVec> Path(SaVec launch, SaVec legEnd, SaVec intercept, WireAmmo a, SmartParams p)
        {
            var path = new List<SaVec> { launch };
            if (SaVec.Dist(legEnd, launch) > 1e-6) path.Add(legEnd);
            path.Add(intercept);
            SaVec prev = path[path.Count - 2];
            SaVec dir = Unit(intercept - prev, new SaVec(0, 1));
            path.Add(intercept + dir * Math.Max(p.VetoRadiusNm, a.SeekerRangeNm));
            return path;
        }

        private sealed class Piece { public SaVec A, B; public double Width, AlongStart; }

        private static List<Piece> Pieces(IList<SaVec> path, WireAmmo a, SmartParams p)
        {
            double wide = Math.Max(p.VetoRadiusNm, a.SeekerRangeNm * Math.Sin((a.SeekerFovDeg / 2.0 + p.SeekerFovMarginDeg) * Math.PI / 180.0));
            var res = new List<Piece>();
            double along = 0;
            int n = path.Count;
            for (int i = 0; i + 1 < n; i++)
            {
                SaVec s = path[i], e = path[i + 1];
                double len = SaVec.Dist(s, e);
                if (i == n - 2) res.Add(new Piece { A = s, B = e, Width = wide, AlongStart = along });
                else if (i == n - 3)
                {
                    double r = a.SeekerRangeNm;
                    if (len > r + 1e-9)
                    {
                        SaVec split = e + Unit(s - e, new SaVec(0, 0)) * r;
                        res.Add(new Piece { A = s, B = split, Width = p.VetoRadiusNm, AlongStart = along });
                        res.Add(new Piece { A = split, B = e, Width = wide, AlongStart = along + (len - r) });
                    }
                    else res.Add(new Piece { A = s, B = e, Width = wide, AlongStart = along });
                }
                else res.Add(new Piece { A = s, B = e, Width = p.VetoRadiusNm, AlongStart = along });
                along += len;
            }
            return res;
        }

        private static double DistToSegment(SaVec pt, SaVec a, SaVec b, out double alongNm)
        {
            SaVec ab = b - a;
            double l2 = Dot(ab, ab);
            double t = l2 < 1e-12 ? 0 : Dot(pt - a, ab) / l2;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            SaVec q = a + ab * t;
            alongNm = Math.Sqrt(l2) * t;
            return SaVec.Dist(pt, q);
        }

        /// <summary>ADR-0038 section 7: veto width along the whole polyline, wider where the seeker may be active; Surface needs ASuW, Subsurface needs ASW.</summary>
        public static List<NeutralHitSa> NeutralsOnPath(IList<SaVec> path, IList<NeutralTrack> ns, WireAmmo a, SmartParams p)
        {
            var hits = new List<NeutralHitSa>();
            if (path == null || path.Count < 2 || ns == null) return hits;
            var pieces = Pieces(path, a, p);
            foreach (var nt in ns)
            {
                bool surface = nt.Domain == "Surface", sub = nt.Domain == "Subsurface";
                if (!((surface && a.Asuw) || (sub && a.Asw))) continue;
                double best = double.MaxValue, bestAlong = 0;
                bool inside = false;
                foreach (var pc in pieces)
                {
                    double al;
                    double d = DistToSegment(nt.Pos, pc.A, pc.B, out al);
                    if (d <= pc.Width && (!inside || d < best)) { inside = true; best = d; bestAlong = pc.AlongStart + al; }
                }
                if (inside) hits.Add(new NeutralHitSa { Id = nt.Id, Cls = nt.Cls, Side = nt.Side, Domain = nt.Domain, DistToPathNm = best, AlongNm = bestAlong });
            }
            return hits;
        }

        private static SidePlan BuildSide(SaVec torp, SaVec target, SaVec targetVelKn, WireAmmo a, IList<NeutralTrack> ns, int side, double phi, LegPlan leg, double legNm, SmartParams p)
        {
            double b = SaVec.Bearing(torp, target);
            SaVec legEnd = legNm > 1e-9 ? torp + SaVec.FromBearing(b + side * phi, legNm) : torp;
            SaVec ic = Intercept(legEnd, a.SpeedsKn[0], target, targetVelKn, a.SeekerFovDeg);
            var path = Path(torp, legEnd, ic, a, p);
            return new SidePlan { Side = side, PhiDeg = phi, Leg = leg, Path = path, Hits = NeutralsOnPath(path, ns, a, p) };
        }

        /// <summary>Both sides; [0] is the preferred clean one (target heading side, else UID parity), else the preferred side with hits.</summary>
        public static SidePlan[] PlanSides(SaVec torp, SaVec target, SaVec targetVelKn, WireAmmo a, IList<NeutralTrack> ns, int uid, SmartParams p)
        {
            double phi = ClampOffset(p.OffsetDeg, p);
            double d = SaVec.Dist(torp, target);
            LegPlan leg = PlanLeg(a, d, phi, targetVelKn.Len, p);
            double effLeg = leg.Skipped ? 0 : leg.LegNm;
            var left = BuildSide(torp, target, targetVelKn, a, ns, -1, phi, leg, effLeg, p);
            var right = BuildSide(torp, target, targetVelKn, a, ns, +1, phi, leg, effLeg, p);

            // preferred side: the way the target moves (shorter path), else the game's UID parity
            double b = SaVec.Bearing(torp, target) * Math.PI / 180.0;
            double lateral = Dot(targetVelKn, new SaVec(Math.Cos(b), -Math.Sin(b)));      // > 0: moves to the right of the line of sight
            bool preferRight = lateral > 0.5 ? true : (lateral < -0.5 ? false : (uid % 2 == 0));
            SidePlan first = preferRight ? right : left, second = preferRight ? left : right;
            if (first.Hits.Count == 0 || second.Hits.Count > 0) return new[] { first, second };
            return new[] { second, first };
        }

        private static double MinClearance(IList<SaVec> path, IList<NeutralTrack> ns)
        {
            double best = double.MaxValue;
            if (ns == null) return best;
            foreach (var nt in ns)
                for (int i = 0; i + 1 < path.Count; i++)
                {
                    double al;
                    best = Math.Min(best, DistToSegment(nt.Pos, path[i], path[i + 1], out al));
                }
            return best;
        }

        /// <summary>ADR-0038 section 7 item 4: a detour to the other side (or farther from the neutral), L in [1;3], that is clean and in range; null = neutral_unavoidable.</summary>
        public static SidePlan AvoidPlan(SaVec torp, SaVec target, SaVec targetVelKn, double fuelFrac, int stage, WireAmmo a,
                                         IList<NeutralTrack> ns, IList<int> newNeutralIds, SmartParams p)
        {
            double phi = ClampOffset(p.OffsetDeg, p);
            double res = p.FuelReservePct / 100.0;
            if (stage < 0) stage = 0;
            if (stage >= a.SpeedsKn.Length) stage = a.SpeedsKn.Length - 1;
            double vStage = a.SpeedsKn[stage], rStage = a.RangesNm[stage];
            double bearing = SaVec.Bearing(torp, target);
            SidePlan best = null;
            double bestLen = double.MaxValue, bestClear = 0;
            if (!(p.AvoidLegStepNm > 1e-6) || double.IsInfinity(p.AvoidLegStepNm) || double.IsNaN(p.AvoidLegMinNm) || double.IsNaN(p.AvoidLegMaxNm) || double.IsInfinity(p.AvoidLegMaxNm))
                throw new InvalidOperationException("bad avoid leg parameters");
            foreach (int side in new[] { -1, 1 })
            {
                int guard = 0;
                for (double l = p.AvoidLegMinNm; l <= p.AvoidLegMaxNm + 1e-9; l += p.AvoidLegStepNm)
                {
                    if (++guard > MaxLoopSteps) throw new InvalidOperationException("avoid leg search did not end");
                    SaVec legEnd = torp + SaVec.FromBearing(bearing + side * phi, l);
                    SaVec ic = Intercept(legEnd, vStage, target, targetVelKn, a.SeekerFovDeg);
                    double remain = SaVec.Dist(legEnd, ic);
                    double fAfter = fuelFrac - l / rStage;
                    if ((fAfter - res) * rStage < p.RangeMargin * remain) continue;
                    var path = Path(torp, legEnd, ic, a, p);
                    var hits = NeutralsOnPath(path, ns, a, p);
                    if (hits.Count > 0) continue;
                    double total = l + remain;
                    double clear = MinClearance(path, ns);
                    if (best == null || total < bestLen - 0.05 || (total <= bestLen + 0.05 && clear > bestClear))
                    {
                        bestLen = total;
                        bestClear = clear;
                        best = new SidePlan { Side = side, PhiDeg = phi, Leg = new LegPlan { LegNm = l, FuelAfterLeg = fAfter }, Path = path, Hits = hits };
                    }
                }
            }
            return best;
        }

        // ---- self test --------------------------------------------------------------------------------------------

        internal const string AdcapIni =
            "[General]\nTargetType=ASW // can be\nSecondaryTargetType=ASuW\n[Guidance]\nGuidanceType=10\nMidCourseCorrection=2 // 2 = wire\n" +
            "TerminalVelocity=65.0\nSpeedSettings=28,40,55,65 // kn\nSpeedSettingRanges=27,20,14,8.5\nWireControlsDepth=True\nWireBreakSpeed=20\n" +
            "WireMinSeafloorClearance=50\nMaxDepth=2800\nSeekerFOV=60.0\nSeekerActiveRange=2\nSeekerPassiveRange=2\nFractionOfRangeToActivateSeeker=0.5\n";

        internal const string UgstIni =
            "[General]\nTargetType=ASW\nSecondaryTargetType=ASuW\n[Guidance]\nGuidanceType=10\nMidCourseCorrection=2\nTerminalVelocity=67.0\n" +
            "MaxVelocity=50.0\nMaxDepth=1700\nSpeedSettings=28,40,55,65\nSpeedSettingRanges=25,20,14,8.5\nWireControlsDepth=True\nWireBreakSpeed=20\n" +
            "WireMinSeafloorClearance=50\nSeekerFOV=60.0\nSeekerActiveRange=2.5\nSeekerPassiveRange=2.5\nFractionOfRangeToActivateSeeker=0.8\n";

        // synthetic Test-71M-like: ASW only, 25/40 kn, 13.5/8.1 miles, break at 15 kn
        internal const string SynthIni =
            "TargetType=ASW\nGuidanceType=8\nMidCourseCorrection=2\nSpeedSettings=25,40\nSpeedSettingRanges=13.5,8.1\nWireControlsDepth=False\n" +
            "WireBreakSpeed=15\nWireMinSeafloorClearance=100\nMaxDepth=1000\nSeekerFOV=60\nSeekerActiveRange=1\nSeekerPassiveRange=1\nFractionOfRangeToActivateSeeker=0.8\n";

        private static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }

        private static TrackSample Ts(double t, double course, double kn, double? depth = null)
        {
            return new TrackSample { T = t, Pos = new SaVec(0, 0), VelKn = SaVec.FromBearing(course, kn), DepthFt = depth };
        }

        // ---------- refusal texts and icon pick (T-113 stream X) ----------

        // codes of Start refusals; texts are UiText keys smart.refuse.<code>
        public static readonly string[] RefusalCodes =
        {
            "out_of_range", "no_wire_torpedo", "no_free_wire", "target_not_allowed", "no_track", "bearing_only", "track_bearing_only",
            "neutral_near", "neutral_guard", "neutral_check_failed", "busy", "not_sub", "no_unit", "geometry_error", "bad_args", "no_wire_depth", "evading",
        };

        /// <summary>Player-visible text of a refusal of Start in the UI language (UiText keys smart.refuse.prefix / smart.refuse.&lt;code&gt;); an unknown code gives a generic text with the code in brackets.</summary>
        public static string RefusalText(string code, string detail)
        {
            string prefix = UiText.Get("smart.refuse.prefix");
            string reason;
            if (Array.IndexOf(RefusalCodes, code) >= 0) reason = UiText.Get("smart.refuse." + code);
            else reason = UiText.Get("smart.refuse.unknown") + " (" + code + ")";
            string res = prefix + ": " + reason;
            if (!string.IsNullOrEmpty(detail)) res += " (" + detail + ")";
            return res;
        }

        /// <summary>Index of the icon centre nearest to (x,y) within tolPx (first on a tie), -1 = none.</summary>
        public static int NearestIcon(IList<double[]> centersPx, double x, double y, double tolPx)
        {
            int best = -1; double bd = double.MaxValue;
            if (centersPx == null) return -1;
            for (int i = 0; i < centersPx.Count; i++)
            {
                var c = centersPx[i];
                if (c == null || c.Length < 2) continue;
                double dx = c[0] - x, dy = c[1] - y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= tolPx && d < bd) { bd = d; best = i; }
            }
            return best;
        }

        private static string RefusalSelfTest()
        {
            string ru = RefusalText("out_of_range", "63 nm");
            if (!ru.StartsWith("Умная атака не начата: цель вне дальности") || !ru.Contains("63 nm")) return "refusal ru: " + ru;
            string en;
            using (UiText.Push("en")) en = RefusalText("out_of_range", null);
            if (en.Contains("out_of_range") || !en.Contains("out of range")) return "refusal en: " + en;
            string unk = RefusalText("zzz", null);
            if (!unk.Contains("(zzz)")) return "refusal unknown: " + unk;
            foreach (var c in new[] { "out_of_range", "no_wire_torpedo", "no_free_wire", "target_not_allowed", "no_track", "bearing_only", "track_bearing_only", "neutral_near", "neutral_guard", "neutral_check_failed", "busy", "not_sub", "no_unit", "geometry_error", "bad_args", "evading" })
            {
                if (Array.IndexOf(RefusalCodes, c) < 0) return "refusal code missing " + c;
                if (RefusalText(c, null).Contains("(" + c + ")")) return "refusal generic for " + c;
                using (UiText.Push("en")) { if (RefusalText(c, null).Contains("(" + c + ")")) return "refusal generic (en) for " + c; }
            }
            var pts = new List<double[]> { new[] { 100.0, 100.0 }, new[] { 200.0, 200.0 } };
            if (NearestIcon(pts, 200, 210, 16) != 1) return "NearestIcon 10px";
            if (NearestIcon(pts, 200, 230, 16) != -1) return "NearestIcon 30px";
            var tie = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 } };
            if (NearestIcon(tie, 1, 1, 16) != 0) return "NearestIcon tie";
            return null;
        }

        internal const string LogPrefix = "[SmartAttack] ";

        /// <summary>One "[SmartAttack] " prefix per log line, never two (live 2026-10-21: "[SmartAttack] [SmartAttack] sa-1 ...").</summary>
        public static string LogPrefixed(string line)
        {
            line = line ?? "";
            while (line.StartsWith(LogPrefix, StringComparison.Ordinal)) line = line.Substring(LogPrefix.Length);
            return LogPrefix + line;
        }

        public static string SelfTest()
        {
            if (LogPrefixed("sa-1 x") != "[SmartAttack] sa-1 x") return "log prefix";
            if (LogPrefixed("[SmartAttack] [SmartAttack] sa-1 x") != "[SmartAttack] sa-1 x") return "log prefix doubled";
            string rf = RefusalSelfTest();
            if (rf != null) return rf;
            string lg = LoopGuardSelfTest();
            if (lg != null) return lg;
            return SelfTestCore();
        }

        private static string LoopGuardSelfTest()
        {
            var adcap = WireAmmo.FromIni("adcap", AdcapIni);
            Func<Action, bool> throws = act => { try { act(); return false; } catch (InvalidOperationException) { return true; } };
            var p = new SmartParams { LegNm = 1e300 };
            if (!throws(() => PlanLeg(adcap, 10, 60, 5, p))) return "huge LegNm must be refused";
            p = new SmartParams { LegNm = double.NaN };
            if (!throws(() => PlanLeg(adcap, 10, 60, 5, p))) return "NaN LegNm must be refused";
            p = new SmartParams { AvoidLegStepNm = 0 };
            if (!throws(() => AvoidPlan(new SaVec(0, 0), new SaVec(0, 10), new SaVec(0, 0), 1.0, 0, adcap, new List<NeutralTrack>(), new List<int>(), p))) return "AvoidLegStepNm=0 must be refused";
            p = new SmartParams { AvoidLegMaxNm = 1e15 };
            if (!throws(() => AvoidPlan(new SaVec(0, 0), new SaVec(0, 10), new SaVec(0, 0), 1.0, 0, adcap, new List<NeutralTrack>(), new List<int>(), p))) return "huge avoid range must be capped";
            return null;
        }

        private static string SelfTestCore()
        {
            var p = new SmartParams();
            var adcap = WireAmmo.FromIni("adcap", AdcapIni);
            var ugst = WireAmmo.FromIni("wp_ugst_1", UgstIni);
            var t71 = WireAmmo.FromIni("synth_t71", SynthIni);

            // --- A2 ammunition
            if (SmartAttackMath.ValidateAmmo(adcap) != null || ValidateAmmo(ugst) != null || ValidateAmmo(t71) != null) return "valid ammo rejected";
            if (!adcap.WireGuided || !adcap.WireControlsDepth || !adcap.Asw || !adcap.Asuw) return "adcap flags";
            if (!t71.Asw || t71.Asuw || t71.WireControlsDepth) return "synthetic flags";
            if (!Near(ugst.ActivateRadiusNm, 2.0, 1e-9) || !Near(adcap.ActivateRadiusNm, 1.0, 1e-9)) return "activate radius";
            if (ugst.SpeedsKn.Length != 4 || ugst.RangesNm[3] != 8.5 || ugst.MaxDepthFt != 1700 || ugst.SeekerFovDeg != 60) return "ugst values";
            if (!Near(RangeAtNm(adcap, 34), 23.5, 1e-9) || RangeAtNm(adcap, 10) != 27 || RangeAtNm(adcap, 99) != 8.5) return "RangeAtNm";
            if (ValidateAmmo(new WireAmmo { WireGuided = true, SpeedsKn = new double[0], RangesNm = new double[0] }) == null) return "no speeds accepted";
            if (ValidateAmmo(new WireAmmo { WireGuided = true, SpeedsKn = new[] { 25.0, 40 }, RangesNm = new[] { 10.0 }, SeekerRangeNm = 1, ActivateFraction = .5, SeekerFovDeg = 60 }) == null) return "length mismatch accepted";
            if (ValidateAmmo(new WireAmmo { WireGuided = true, SpeedsKn = new[] { 25.0 }, RangesNm = new[] { 0.0 }, SeekerRangeNm = 1, ActivateFraction = .5, SeekerFovDeg = 60 }) == null) return "range 0 accepted";
            var notWire = WireAmmo.FromIni("x", AdcapIni.Replace("MidCourseCorrection=2", "MidCourseCorrection=0"));
            if (ValidateAmmo(notWire) == null || !ValidateAmmo(notWire).StartsWith("ammo_params_invalid:")) return "non-wire accepted";

            // --- A3 range
            bool fs;
            double pL4 = PathAfterLeg(15, 4, 60, 0, 28);
            if (!Near(pL4, 13.454, 0.01)) return "P(L=4) " + pL4;
            if (FinalStage(adcap, 1 - 4.0 / 27, pL4, p, out fs) != 0 || fs) return "ADCAP D=15 L=4 must be stage 0";
            double pL3 = PathAfterLeg(10, 3, 60, 0, 28);
            if (FinalStage(adcap, 1 - 3.0 / 27, pL3, p, out fs) != 2 || fs) return "ADCAP D=10 L=3 must be 55 kn";
            if (FinalStage(ugst, 1 - 3.0 / 25, pL3, p, out fs) != 2 || fs) return "UGST D=10 L=3 must be 55 kn";
            var capped = WireAmmo.FromIni("c", AdcapIni + "MaxSpeedOnWire=40\n");
            if (FinalStage(capped, 1 - 3.0 / 27, pL3, p, out fs) != 1) return "MaxSpeedOnWire cap";
            if (FinalStage(adcap, 0.3, 20, p, out fs) != 0 || !fs) return "fuel short";
            var lp = PlanLeg(t71, 10, 60, 0, p);
            if (!(lp.LegNm >= 0.85 && lp.LegNm < 1.0) || !lp.Skipped || lp.OutOfRange) return "Test-71M leg " + lp.LegNm + " skipped=" + lp.Skipped;
            var la = PlanLeg(adcap, 15, 60, 0, p);
            if (!Near(la.LegNm, 3, 1e-9) || la.Skipped || la.OutOfRange) return "ADCAP D=15 leg " + la.LegNm;
            if (!PlanLeg(adcap, 40, 60, 0, p).OutOfRange) return "out of range";
            if (!Near(FuelFraction(10, 20), 0.5, 1e-9) || FuelFraction(30, 20) != 1) return "FuelFraction";

            // --- A4 intercept, carrot, shift
            var o = new SaVec(0, 0);
            SaVec ic0 = Intercept(o, 28, new SaVec(0, 10), new SaVec(0, 0), 60);
            if (SaVec.Dist(ic0, new SaVec(0, 10)) > 1e-9) return "intercept of a stationary target";
            SaVec ic1 = Intercept(o, 28, new SaVec(5, 10), new SaVec(-10, 0), 60);
            if (!(ic1.X < 5 - 0.1) || !Near(SaVec.Dist(o, ic1) / 28, SaVec.Dist(ic1, new SaVec(5, 10)) / 10, 0.02)) return "intercept lead " + ic1.X + "," + ic1.Y;
            SaVec ic2 = Intercept(o, 28, new SaVec(0, 10), new SaVec(60, 0), 60);       // too fast: clamp keeps the angle <= FOV/2.5
            if (Math.Abs(Norm180(SaVec.Bearing(o, ic2))) > 24.0 + 1e-6) return "FOV clamp " + SaVec.Bearing(o, ic2);
            if (!Near(CarrotDistance(10, adcap, p), 3, 1e-9) || !Near(CarrotDistance(4, adcap, p), 2, 1e-9) || !Near(CarrotDistance(2, adcap, p), 1.5, 1e-9)) return "carrot ADCAP";
            if (!Near(CarrotDistance(3, ugst, p), 2.5, 1e-9)) return "carrot UGST";
            if (ClampOffset(30, p) != 45 || ClampOffset(60, p) != 60) return "ClampOffset";
            double dir;
            SaVec cp = CarrotPoint(o, new SaVec(0, 10), +1, 60, 3, null, p, out dir);
            if (!Near(dir, 60, 1e-6) || !Near(cp.X, 3 * Math.Sin(Math.PI / 3), 1e-6)) return "carrot right";
            cp = CarrotPoint(o, new SaVec(0, 10), -1, 60, 3, null, p, out dir);
            if (!Near(dir, 300, 1e-6)) return "carrot left " + dir;
            cp = CarrotPoint(o, new SaVec(0, 10), +1, 60, 3, 20.0, p, out dir);
            if (!Near(dir, 30, 1e-6)) return "carrot rate limit " + dir;
            foreach (double vx in new[] { 0.0, 8.0, -12.0 })
            {
                var tp = new SaVec(1, 10);
                var tv = new SaVec(vx, 3);
                SaVec car = CarrotPoint(o, tp, +1, 60, 3, null, p, out dir);
                SaVec sh = ShiftFor(car, tp, tv, o, 28, 10, p);
                if (sh.Len > 1.5 * 10 + 1e-9) return "shift too long";
                SaVec back = Intercept(o, 28, tp + sh, tv, 60);
                if (SaVec.Dist(back, car) > 0.01) return "ShiftFor/Intercept mismatch " + SaVec.Dist(back, car) + " vx=" + vx;
            }
            SaVec shs = ShiftFor(CarrotPoint(o, new SaVec(0, 10), +1, 60, 3, null, p, out dir), new SaVec(0, 10), new SaVec(0, 0), o, 28, 10, p);
            if (shs.Len < 0.71 * 10) return "shift shorter than 0.71 D: " + shs.Len;

            // --- A5 detector, cone
            var h = new List<TrackSample>();
            for (int t = 0; t <= 140; t++) h.Add(Ts(t, t < 100 ? 90 : (t < 120 ? 90 + (t - 100) : 110), 12));
            if (Noticed(h.Where(s => s.T <= 125).ToList(), 50, 125, false, p) == null || Noticed(h.Where(s => s.T <= 125).ToList(), 50, 125, false, p).Kind != "course") return "course 20 deg not noticed";
            h.Clear();
            for (int t = 0; t <= 140; t++) h.Add(Ts(t, t < 100 ? 90 : (t < 120 ? 90 + (t - 100) / 2.0 : 100), 12));
            if (Noticed(h.Where(s => s.T <= 125).ToList(), 50, 125, false, p) != null) return "course 10 deg noticed";
            h.Clear();
            var rnd = new Random(7);
            for (int t = 0; t <= 140; t++) h.Add(Ts(t, (t % 2 == 0 ? 80 : 100) + (t >= 100 ? 20 : 0), 12));
            if (Noticed(h.Where(s => s.T <= 125).ToList(), 50, 125, false, p) != null) return "noisy base noticed";
            h.Clear();
            for (int t = 0; t <= 140; t++) h.Add(Ts(t, 90, t < 100 ? 12 : Math.Min(16, 12 + (t - 100) * 0.1)));
            var sp = Noticed(h, 50, 140, false, p);
            if (sp == null || sp.Kind != "speed") return "speed 12->16 not noticed";
            h.Clear();
            for (int t = 0; t <= 140; t++) h.Add(Ts(t, 90, t < 100 ? 12 : Math.Min(14, 12 + (t - 100) * 0.05)));
            if (Noticed(h, 50, 140, false, p) != null) return "speed 12->14 noticed";
            h.Clear();
            for (int t = 0; t <= 140; t++) h.Add(Ts(t, 90, 12, t < 90 ? 150 : Math.Min(260, 150 + (t - 90) * 2.2)));
            var dp = Noticed(h, 50, 140, true, p);
            if (dp == null || dp.Kind != "depth") return "depth 150->260 not noticed";
            if (Noticed(h, 50, 140, false, p) != null) return "depth checked for a ship";
            h.Clear();
            for (int t = 0; t <= 140; t++) h.Add(Ts(t, t < 100 ? 90 : (t < 120 ? 90 + (t - 100) : 110), 12));
            if (Noticed(h, 130, 140, false, p) != null) return "change before launchT counted";
            if (!InCone(10, o, new SaVec(0, 10), 35) || InCone(40, o, new SaVec(0, 10), 35) || !InCone(350, o, new SaVec(0, 10), 35)) return "InCone";

            // --- A6 layer and depths
            var pd = PlanDepths(false, 300, true, null, 1500, adcap, 40, null, p);
            if (pd.OwnPreset != 4 || pd.NoDive || pd.TorpDepthFt != 330) return "ship under layer: " + pd.OwnPreset + "/" + pd.TorpDepthFt;
            pd = PlanDepths(false, null, true, null, 1500, adcap, 40, null, p);
            if (!pd.NoDive || pd.OwnPreset != null || !pd.Warnings.Contains("no_layer")) return "ship no layer";
            pd = PlanDepths(true, 300, true, 150, 1500, adcap, 40, null, p);
            if (pd.OwnPreset != 4 || pd.TorpDepthFt != 330) return "sub above layer";
            pd = PlanDepths(true, 300, true, 500, 1500, adcap, 40, null, p);
            if (pd.OwnPreset != 3 || pd.TorpDepthFt != 270) return "sub below layer";
            pd = PlanDepths(true, 300, true, null, 1500, adcap, 40, null, p);
            if (pd.OwnPreset != null || pd.TorpDepthFt != 330 || !pd.Warnings.Contains("depth_unknown")) return "sub depth unknown";
            pd = PlanDepths(true, 300, false, 150, 1500, adcap, 40, null, p);
            if (pd.OwnPreset != null || !pd.Warnings.Contains("depth_unknown")) return "other tile";
            pd = PlanDepths(false, 300, true, null, 350, adcap, 40, null, p);
            if (MaxOwnDepthFt(350, adcap, p) != 280 || !pd.NoDive || pd.OwnPreset != null) return "layer deeper than the bottom limit";
            if (ClampTorpDepth(10, 40, 2800, false, p) != 40 || ClampTorpDepth(9000, 40, 2800, false, p) != 2800 || ClampTorpDepth(5, 0, 2800, true, p) != 30) return "ClampTorpDepth";
            pd = PlanDepths(true, 300, true, 150, 1500, t71, 40, null, p);
            if (pd.Error != "no_wire_depth") return "no_wire_depth for a sub target";
            pd = PlanDepths(false, 300, true, null, 1500, t71, 40, null, p);
            if (pd.Error != null || pd.TorpDepthFt != null || !pd.Warnings.Contains("no_wire_depth")) return "no_wire_depth for a ship";
            pd = PlanDepths(true, 300, true, 150, 1500, adcap, 40, 4, p);
            if (pd.OwnPreset != null) return "already at the preset";

            // --- A7 corridor and sides
            var legEnd = SaVec.FromBearing(60, 3);
            var tgt = new SaVec(0, 10);
            var path = Path(o, legEnd, tgt, adcap, p);
            if (path.Count != 4 || !Near(SaVec.Dist(path[3], path[2]), 2.0, 1e-6)) return "path shape " + path.Count;
            var nearEnd = new NeutralTrack { Id = 1, Domain = "Surface", Side = "neutral", Pos = legEnd + new SaVec(1, 0) };
            if (NeutralsOnPath(path, new[] { nearEnd }, adcap, p).Count != 1) return "neutral near the leg end";
            if (NeutralsOnPath(Path(o, o, tgt, adcap, p), new[] { nearEnd }, adcap, p).Count != 0) return "direct corridor must not see it";
            var subN = new NeutralTrack { Id = 2, Domain = "Subsurface", Side = "unknown", Pos = new SaVec(0, 6) };
            if (NeutralsOnPath(path, new[] { subN }, adcap, p).Count != 1) return "sub neutral vs ASW";
            var asuwOnly = WireAmmo.FromIni("asuw", AdcapIni.Replace("TargetType=ASW", "TargetType=ASuW"));
            if (NeutralsOnPath(path, new[] { subN }, asuwOnly, p).Count != 0) return "sub neutral vs ASuW-only";
            var lastSeg = SaVec.FromBearing(60, 3);
            SaVec side24 = new SaVec(2.4, 0);
            var wide = new NeutralTrack { Id = 3, Domain = "Surface", Side = "neutral", Pos = new SaVec(side24.X, 9) };
            var widePath = new List<SaVec> { o, new SaVec(0, 5), tgt, new SaVec(0, 12) };
            if (NeutralsOnPath(widePath, new[] { wide }, adcap, p).Count != 0) return "2.4 side ADCAP";
            if (NeutralsOnPath(widePath, new[] { wide }, ugst, p).Count != 0) return "2.4 side UGST";
            var r5 = WireAmmo.FromIni("r5", AdcapIni.Replace("SeekerActiveRange=2", "SeekerActiveRange=5").Replace("SeekerPassiveRange=2", "SeekerPassiveRange=5"));
            if (NeutralsOnPath(Path(o, o, tgt, r5, p), new[] { wide }, r5, p).Count != 1) return "2.4 side R=5";
            var beyond = new NeutralTrack { Id = 4, Domain = "Surface", Side = "neutral", Pos = new SaVec(0, 11.5) };
            if (NeutralsOnPath(path, new[] { beyond }, adcap, p).Count != 1) return "neutral in the overshoot";
            var leftN = new NeutralTrack { Id = 5, Domain = "Surface", Side = "neutral", Pos = SaVec.FromBearing(-60, 3) + new SaVec(-1, 0) };
            var rightN = new NeutralTrack { Id = 6, Domain = "Surface", Side = "neutral", Pos = legEnd + new SaVec(1, 0) };
            var sp1 = PlanSides(o, tgt, new SaVec(0, 0), adcap, new[] { leftN }, 2, p);
            if (sp1[0].Side != +1 || sp1[0].Hits.Count != 0 || sp1[1].Side != -1 || sp1[1].Hits.Count == 0) return "neutral on the left -> right";
            sp1 = PlanSides(o, tgt, new SaVec(0, 0), adcap, new[] { rightN }, 2, p);
            if (sp1[0].Side != -1) return "neutral on the right -> left";
            sp1 = PlanSides(o, tgt, new SaVec(6, 0), adcap, new NeutralTrack[0], 3, p);
            if (sp1[0].Side != +1) return "target moves right -> right";
            sp1 = PlanSides(o, tgt, new SaVec(-6, 0), adcap, new NeutralTrack[0], 2, p);
            if (sp1[0].Side != -1) return "target moves left -> left";
            if (PlanSides(o, tgt, new SaVec(0, 0), adcap, new NeutralTrack[0], 2, p)[0].Side != +1 || PlanSides(o, tgt, new SaVec(0, 0), adcap, new NeutralTrack[0], 3, p)[0].Side != -1) return "UID parity";
            sp1 = PlanSides(o, tgt, new SaVec(0, 0), adcap, new[] { leftN, rightN }, 2, p);
            if (sp1[0].Hits.Count == 0 || sp1[1].Hits.Count == 0) return "both sides blocked";

            // --- A8 AvoidPlan
            var av = AvoidPlan(o, tgt, new SaVec(0, 0), 1.0, 0, adcap, new[] { leftN }, new[] { 5 }, p);
            if (av == null || av.Side != +1 || av.Leg.LegNm < 1 || av.Leg.LegNm > 3 || av.Hits.Count != 0) return "avoid plan";
            if (AvoidPlan(o, tgt, new SaVec(0, 0), 0.2, 0, adcap, new[] { leftN }, new[] { 5 }, p) != null) return "avoid with no fuel";
            if (AvoidPlan(o, tgt, new SaVec(0, 0), 1.0, 0, adcap, new[] { new NeutralTrack { Id = 8, Domain = "Surface", Pos = new SaVec(-1.2, 5) }, new NeutralTrack { Id = 9, Domain = "Surface", Pos = new SaVec(1.2, 5) } }, new[] { 8, 9 }, p) != null) return "avoid with neutrals on both sides";
            var av2 = AvoidPlan(o, tgt, new SaVec(0, 0), 0.8, 2, adcap, new[] { leftN }, new[] { 5 }, p);
            if (av2 != null && av2.Leg.FuelAfterLeg >= 0.8) return "avoid keeps the stage fuel";

            return LeakCheck();
        }

        // ADR-0038 8.5 / A9: SmartAttackMath takes only plain values and the pure DTOs - never game objects.
        private static string LeakCheck()
        {
            var allowed = new HashSet<Type>
            {
                typeof(double), typeof(int), typeof(bool), typeof(string), typeof(SaVec), typeof(TrackSample), typeof(WireAmmo),
                typeof(double[]), typeof(SmartParams), typeof(NeutralTrack), typeof(LegPlan), typeof(DepthPlan), typeof(SidePlan), typeof(Notice), typeof(NeutralHitSa),
            };
            foreach (MethodInfo m in typeof(SmartAttackMath).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                foreach (ParameterInfo pi in m.GetParameters())
                {
                    Type t = pi.ParameterType;
                    if (t.IsByRef) t = t.GetElementType();
                    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>)) t = Nullable.GetUnderlyingType(t);
                    if (t.IsArray) t = t.GetElementType();
                    if (t.IsGenericType) t = t.GetGenericArguments()[0];
                    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>)) t = Nullable.GetUnderlyingType(t);
                    if (!allowed.Contains(t)) return "leak: " + m.Name + "(" + pi.Name + ": " + pi.ParameterType + ")";
                }
            }
            return null;
        }
    }
}
