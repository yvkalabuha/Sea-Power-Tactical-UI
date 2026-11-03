using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 section 1: the leak model of the AA (T-340 recon sections 4 and 10.2): P(N), the smallest N, the port of the game's interception chance,
    // the missile correction, the track through the zones of the neighbours, the fallback for ships without the points cache. Pure.
    internal static class StrikeAdMath
    {
        /// <summary>The floor of the game (damage.ini InterceptChanceFloor).</summary>
        public const double InterceptFloor = 0.05;
        private const double Ft = 0.00453571;   // feet -> Unity units, as in the recon port

        /// <summary>p = min(K/S, PCap); m = max(1, S/N); q = 1 - (1-p)^m; P = 1 - q^N (the chance that at least one of N simultaneous missiles leaks).</summary>
        public static double Leak(double n, double k, double s)
        {
            if (n <= 0) return 0;
            if (s <= 0 || k <= 0) return 1.0;
            double p = Math.Min(k / s, StrikeCalib.PCap), m = Math.Max(1.0, s / n);
            double q = 1 - Math.Pow(1 - p, m);
            return 1 - Math.Pow(q, n);
        }

        public static int Need(double k, double s, double threshold, int cap = 200)
        {
            if (k <= 0 || s <= 0) return 1;
            for (int n = 1; n < cap; n++) if (Leak(n, k, s) >= threshold) return n;
            return cap;
        }

        /// <summary>The leak through the zones of the track (ADR-0132 plan section 4.3): sigma = product of the per-zone survival, P = 1 - (1 - sigma)^N.</summary>
        public static double PathLeak(int n, IList<StrikeZone> zones)
        {
            if (n <= 0) return 0;
            return 1 - Math.Pow(1 - PathSigma(n, zones), n);
        }

        /// <summary>T-342: the leak of ONE missile of a salvo of n (PathLeak == 1 - (1 - PathSigma)^n).</summary>
        public static double PathSigma(int n, IList<StrikeZone> zones)
        {
            if (n <= 0) return 0;
            double nj = n, sigma = 1;
            if (zones != null)
                foreach (var z in zones)
                {
                    if (z.K <= 0 || z.S <= 0) continue;
                    double w = z.IsTarget ? 1 : z.W, c = z.IsTarget ? 1 : z.C * (1 - Math.Min(1, Math.Max(0, z.Gone)));
                    double sp = z.S * w * c;
                    if (sp <= 0) continue;
                    double p = Math.Min(z.KScaled / z.S, StrikeCalib.PCap);
                    double m = z.IsTarget ? Math.Max(1.0, sp / nj) : sp / nj;
                    double surv = Math.Pow(1 - p, m);
                    sigma *= surv;
                    nj = Math.Max(1e-9, nj * surv);
                }
            return sigma;
        }

        public static int PathNeed(IList<StrikeZone> zones, double threshold, int cap = 200)
        {
            bool any = false;
            if (zones != null) foreach (var z in zones) if (z.IsTarget && z.K > 0 && z.S > 0) any = true;
            if (!any) return 1;
            for (int n = 1; n < cap; n++) if (PathLeak(n, zones) >= threshold) return n;
            return cap;
        }

        /// <summary>f = the calibration scale of a ship: ClassScale(class) x the game difficulty (1 - Globals._missileInterceptChanceReduction), the latter clamped. The cache of the game is built without the difficulty.</summary>
        public static double Scale(string classIni, double difficulty)
        {
            double d = StrikeCalib.UseDifficulty ? Math.Max(StrikeCalib.DifficultyMin, Math.Min(StrikeCalib.DifficultyMax, difficulty > 0 ? difficulty : 1.0)) : 1.0;
            return StrikeCalib.ClassScale(classIni) * d;
        }

        /// <summary>The "clearing" rule: with clearGain 1 a neighbour whose own P reaches the threshold is skipped (skip = true), otherwise its zone is weighted (1 - clearGain x P).</summary>
        public static StrikeZone Cleared(StrikeZone z, double neighbourP, double threshold, double clearGain, out bool skip)
        {
            skip = clearGain >= 0.999 && neighbourP >= threshold;
            z.IsCleared = true;
            z.Gone = skip ? 1 : Math.Min(1, Math.Max(0, clearGain * neighbourP));   // the zone stays in the list (the log needs it to fit the gain); a zero share changes nothing in the leak
            return z;
        }

        public static StrikeZone Cleared(StrikeZone z, double neighbourP, double threshold, out bool skip)
        {
            return Cleared(z, neighbourP, threshold, StrikeCalib.ClearGain, out skip);
        }

        // ---- port of Utils.GetInterceptionChance (stable 0.8.7-261021; tools/bench/T-340-strike-recon/intercept_pk.py), without the difficulty bonus ----
        /// <summary>The chance that one SAM shot kills a missile at altFt, kn (T-340 section 4.1). Floor 0.05.</summary>
        public static double InterceptChance(StrikeSam sam, double altFt, double kn, double frontalM2, double massKg, double defBonus)
        {
            if (sam == null) return 0;
            double kp = sam.Pk;
            double sizeMult = sam.SizeMult, speedMult = sam.SpeedMult, ooa = sam.OutOfAltPen;
            double num4 = frontalM2 > 0 ? Math.Pow(frontalM2 / 0.0047, 1.5) : massKg / 2;
            double size = num4 > 0 ? (1 - Math.Sqrt(num4) / 122) / 5 : 0.2;
            size = Math.Max(-0.5, Math.Min(0.5, size * sizeMult));
            bool finite = sam.MaxAttackKn > 0;
            double maxAtt = finite ? sam.MaxAttackKn : double.PositiveInfinity;
            double n6 = finite ? maxAtt : 670.0, n7 = finite ? maxAtt / 2 : 670.0;
            double spd = 0;
            if (kn > n7)
            {
                spd = (Math.Min(kn, maxAtt) - n6 / 2) / (n6 * 2);
                if (finite && kn > n6) spd += (kn - n6) / n6;
                spd = Math.Min(0.5, 0.5 * speedMult * spd);
            }
            double lo = sam.MinAltFt * Ft, hi = sam.MaxAltFt * Ft, alt = altFt * Ft, alp = 0;
            if (alt < lo)
                alp = ooa * Math.Min(Math.Sqrt(Math.Min((lo - alt) / lo, 1.0)) * 0.8 + 0.2, 1.0);
            else if (alt > hi)
                alp = ooa * Math.Sqrt(Math.Min((alt - hi) * 10 / hi, 1.0));
            else
            {
                double n9 = Math.Min(lo * 3, lo + 0.907142);
                if (alt < n9) alp = (n9 - alt) / (n9 - lo) * (0.2 * ooa);
            }
            double bonus = defBonus > 1 ? defBonus / 100 : defBonus;
            double p = kp * (1 - 2 * spd) * (1 - 2 * size) * (1 - 2 * alp) * (1 - bonus);
            return Math.Max(p, InterceptFloor);
        }

        /// <summary>f = mean over the SAMs of (chance against this missile / chance against the reference raid); no SAM data = 1. Reference: 350 kg 500 kn 15 ft (sub) or 2000 kg 1100 kn 33 ft (supersonic).</summary>
        public static double MissileFactor(IList<StrikeSam> sams, StrikeMissile m, bool supersonic)
        {
            if (sams == null || sams.Count == 0 || m == null) return 1;
            double sum = 0; int n = 0;
            foreach (var s in sams)
            {
                if (s == null) continue;
                // the missile as the game's chance sees it (the terminal altitude and speed), the reference raid as ReferenceTargets keeps it: sub 350 kg 500 kn 15 ft bonus 0, sup 2000 kg 1100 kn 32 ft bonus 0.1 (the mass is the effective one: the port halves, so 2x is passed)
                double alt = m.ProfileAltFt > 0 ? m.ProfileAltFt : (m.CruiseAltFt > 0 ? m.CruiseAltFt : 50), kn = m.ProfileKn > 0 ? m.ProfileKn : (m.SpeedKn > 0 ? m.SpeedKn : 540);
                double pm = InterceptChance(s, alt, kn, m.FrontalM2, m.MassKg, m.DefBonus);
                double pr = supersonic ? InterceptChance(s, 32, 1100, 0, 4000, 0.1) : InterceptChance(s, 15, 500, 0, 700, 0);
                if (pr <= 0) continue;
                sum += pm / pr; n++;
            }
            return n == 0 ? 1 : Math.Max(0.05, Math.Min(3.0, sum / n));
        }

        private static bool RoleIs(string role, params string[] names)
        {
            if (role == null) return false;
            foreach (string n in names) if (string.Equals(role, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>No cache entry: AAW_Capability (+1 above 4000 t, +1 carrier), else the role. K = capacity, S = 3K (the shape of the cached values).</summary>
        public static StrikeAd Fallback(int aawCap, double dispT, bool carrier, string role)
        {
            double k; string src;
            if (RoleIs(role, "Merchant", "SmallCivilian", "Landing", "RAS")) { k = 0; src = "role"; }
            else if (aawCap >= 0) { k = aawCap + 1 + (dispT > 4000 ? 1 : 0) + (carrier ? 1 : 0); src = "aaw"; }
            else if (RoleIs(role, "FAC", "Minesweep")) { k = 1.5; src = "role"; }
            else if (RoleIs(role, "AAW", "ASuW_VLR")) { k = 10; src = "role"; }
            else { k = 3; src = "none"; }   // nothing is known: a middle ship, on the careful side
            double s = k * 3;
            return new StrikeAd { Ksub = k, Ssub = s, Ksup = k, Ssup = s, Source = src };
        }

        private static bool Get(IDictionary<string, double> d, string k, out double v) { return d.TryGetValue(k, out v); }

        /// <summary>The cached roles of the game points: aa_k_subskim / aa_s_subskim (required), aa_k_supskim / aa_s_supskim (default = sub). Source "cache" or "none".</summary>
        private const string KSub = "aa_k_subskim", SSub = "aa_s_subskim", KSup = "aa_k_supskim", SSup = "aa_s_supskim";

        public static StrikeAd FromRoles(IDictionary<string, double> roles)
        {
            double ks, ss, kp, sp;
            if (roles == null || !Get(roles, KSub, out ks) || !Get(roles, SSub, out ss)) return new StrikeAd { Source = "none" };
            if (!Get(roles, KSup, out kp) || !Get(roles, SSup, out sp)) { kp = ks; sp = ss; }
            return new StrikeAd { Ksub = ks, Ssub = ss, Ksup = kp, Ssup = sp, Source = "cache" };
        }

        /// <summary>taskforce_pointvalues.json (Version 3): Units["vessels/&lt;ini&gt;"].Roles. Fills into by the ini name; units without aa_* are skipped.</summary>
        public static bool ParseCache(string json, IDictionary<string, StrikeAd> into, out string err)
        {
            err = null;
            try
            {
                var root = Json.Parse(json) as Dictionary<string, object>;
                if (root == null) { err = "bad_json"; return false; }
                object ver;
                if (!root.TryGetValue("Version", out ver) || !(ver is double) || (int)(double)ver != 3) { err = "version"; return false; }
                var units = root.ContainsKey("Units") ? root["Units"] as Dictionary<string, object> : null;
                if (units == null) { err = "no_units"; return false; }
                foreach (var kv in units)
                {
                    if (!kv.Key.StartsWith("vessels/", StringComparison.Ordinal)) continue;
                    var u = kv.Value as Dictionary<string, object>;
                    var roles = u != null && u.ContainsKey("Roles") ? u["Roles"] as Dictionary<string, object> : null;
                    if (roles == null) continue;
                    var nums = new Dictionary<string, double>();
                    foreach (var r in roles) if (r.Key.StartsWith("aa_k_", StringComparison.Ordinal) || r.Key.StartsWith("aa_s_", StringComparison.Ordinal)) if (r.Value is double) nums[r.Key] = (double)r.Value;
                    var ad = FromRoles(nums);
                    if (ad.Source == "cache") into[kv.Key.Substring("vessels/".Length)] = ad;
                }
                return true;
            }
            catch (Exception e) { err = "bad_json: " + e.Message; return false; }
        }

        // ---- self test ----
        private static readonly object[][] Fleet =
        {   // ini, ksub, ssub, ksup, ssup, need@0.9 sub, need@0.8 sub, need@0.9 sup (T-340 section 10.2, cache of 2026-10-30)
            new object[] { "usn_cg_bunker_hill", 34.2857437, 94.0, 35.7760391, 69.577774, 20, 18, 22 },
            new object[] { "usn_cg_ticonderoga", 18.3163738, 50.2173462, 13.6496897, 26.5461197, 13, 12, 12 },
            new object[] { "wp_bpk_udaloy", 23.1279221, 64.0, 11.9650726, 40.8835754, 15, 14, 10 },
            new object[] { "wp_em_sovremenny", 14.7870512, 22.658226, 7.06631613, 11.666667, 14, 12, 8 },
            new object[] { "usn_cv_kitty_hawk", 9.4852705, 17.7975388, 5.76985788, 11.5780725, 10, 8, 7 },
            new object[] { "usn_ddg_kidd", 8.86082554, 27.714283, 5.15813732, 12.2857141, 8, 7, 6 },
            new object[] { "usn_dd_spruance", 4.74263525, 8.89876938, 3.36400318, 6.28903627, 6, 6, 5 },
            new object[] { "wp_rkr_slava", 4.026196, 29.1428585, 3.73927736, 13.7142859, 5, 4, 5 },
            new object[] { "usn_ffg_oliver_hazard_perry", 3.11741447, 9.54761791, 1.88876057, 4.40476179, 5, 4, 4 },
            new object[] { "wp_takr_kiev", 2.30714083, 19.1813946, 1.49058044, 8.29886627, 4, 3, 3 },
        };

        public static string SelfTest(string part = "all")
        {
            string e;
            if ((part == "all" || part == "ad") && (e = SelfTestAd()) != null) return e;
            if ((part == "all" || part == "pk") && (e = SelfTestPk()) != null) return e;
            if ((part == "all" || part == "cache") && (e = SelfTestCache()) != null) return e;
            if ((part == "all" || part == "calib") && (e = SelfTestCalib()) != null) return e;
            return null;
        }

        /// <summary>Lines "grid n k s leak" for the pytest cross-check against salvo_need.pleak.</summary>
        public static string Grid()
        {
            var sb = new StringBuilder();
            foreach (var f in Fleet)
                for (int n = 1; n <= 30; n++)
                    sb.Append("grid ").Append(n).Append(' ').Append(((double)f[1]).ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                      .Append(((double)f[2]).ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                      .Append(Leak(n, (double)f[1], (double)f[2]).ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        private static string SelfTestAd()
        {
            foreach (var f in Fleet)
            {
                string ini = (string)f[0];
                int n9 = Need((double)f[1], (double)f[2], 0.9), n8 = Need((double)f[1], (double)f[2], 0.8), ns = Need((double)f[3], (double)f[4], 0.9);
                if (n9 != (int)f[5]) return "need 0.9 " + ini + " = " + n9 + ", want " + f[5];
                if (n8 != (int)f[6]) return "need 0.8 " + ini + " = " + n8 + ", want " + f[6];
                if (ns != (int)f[7]) return "need 0.9 sup " + ini + " = " + ns + ", want " + f[7];
            }
            if (Need(0, 10, 0.9) != 1 || Need(5, 0, 0.9) != 1) return "need K=0";
            if (Leak(5, 0, 10) != 1.0) return "leak K=0";
            if (Leak(0, 5, 10) != 0) return "leak N=0";
            if (!(Leak(8, 8.86, 27.7) > Leak(4, 8.86, 27.7))) return "leak grows with N";
            // path: one target zone == Leak
            double k = 8.86082554, s = 27.714283;
            for (int n = 1; n <= 20; n++)
                if (Math.Abs(PathLeak(n, new[] { StrikeZone.Target(k, s) }) - Leak(n, k, s)) > 1e-9) return "path target == leak, n=" + n;
            // T-342: PathLeak == 1 - (1 - PathSigma)^n on a grid (target alone and with a neighbour)
            foreach (var zz in new[] { new[] { StrikeZone.Target(k, s) }, new[] { StrikeZone.Near(3.1, 9.5, 0.5, StrikeCalib.Eta), StrikeZone.Target(k, s) } })
                for (int n = 1; n <= 64; n++)
                    if (Math.Abs(PathLeak(n, zz) - (1 - Math.Pow(1 - PathSigma(n, zz), n))) > 1e-12) return "PathLeak == 1-(1-PathSigma)^n, n=" + n;
            if (PathSigma(0, null) != 0 || PathSigma(5, null) != 1) return "PathSigma edges";
            int need0 = PathNeed(new[] { StrikeZone.Target(k, s) }, 0.9);
            if (need0 != 8) return "path need target only " + need0;
            // neighbour with W=0 changes nothing
            var w0 = new[] { StrikeZone.Near(18, 50, 0, 0.5), StrikeZone.Target(k, s) };
            if (PathNeed(w0, 0.9) != need0) return "neighbour W=0";
            // a neighbour does not decrease the need
            var near = new[] { StrikeZone.Near(3.1, 9.5, 0.5, StrikeCalib.Eta), StrikeZone.Target(k, s) };
            if (PathNeed(near, 0.9) < need0) return "neighbour decreases need";
            var strong = new[] { StrikeZone.Near(18.3, 50.2, 0.5, StrikeCalib.Eta), StrikeZone.Target(k, s) };
            if (PathNeed(strong, 0.9) <= need0) return "strong neighbour must raise the need";
            if (PathNeed(strong, 0.9) < PathNeed(near, 0.9)) return "stronger neighbour raises more";
            if (PathLeak(8, strong) >= PathLeak(8, new[] { StrikeZone.Target(k, s) })) return "neighbour lowers P";
            // clearing: a neighbour with P >= threshold is skipped = no neighbour
            bool skip;
            var z = Cleared(StrikeZone.Near(3.1, 9.5, 0.5, StrikeCalib.Eta), 0.93, 0.9, 1.0, out skip);   // the phase A rule: the gain 1
            if (!skip) return "clearing skip";
            var zs = new List<StrikeZone> { z, StrikeZone.Target(k, s) };   // the skipped zone stays in the list and changes nothing
            if (PathNeed(zs, 0.9) != need0) return "clearing == no neighbour";
            z = Cleared(StrikeZone.Near(3.1, 9.5, 0.5, 0.5), 0.4, 0.9, 1.0, out skip);
            if (skip || Math.Abs(z.C * (1 - z.Gone) - 0.3) > 1e-9) return "clearing weight " + z.C * (1 - z.Gone);
            // fallback
            var fb = Fallback(6, 8300, false, "AAW");
            if (fb.Source != "aaw" || Need(fb.Ksub, fb.Ssub, 0.9) < 6 || Need(fb.Ksub, fb.Ssub, 0.9) > 10) return "fallback aaw " + fb.Source + " " + Need(fb.Ksub, fb.Ssub, 0.9);
            if (Fallback(-1, 20000, false, "Merchant").Ksub != 0) return "fallback merchant";
            if (Fallback(9, 5000, false, "Merchant").Ksub != 0) return "fallback merchant with aaw";
            var fac = Fallback(-1, 300, false, "FAC");
            if (fac.Source != "role" || fac.Ksub <= 0 || fac.Ksub > 2) return "fallback fac";
            if (Fallback(-1, 300, false, null).Source != "none") return "fallback none";
            if (Fallback(2, 4500, true, "AAW").Ksub != 5) return "fallback carrier +2";
            return null;
        }

        private static string SelfTestCalib()
        {
            double k = 8.86082554, s = 27.714283;
            // scale 1 = the phase A numbers
            var z1 = StrikeZone.Target(k, s);
            if (PathNeed(new[] { z1 }, 0.9) != 8) return "calib scale 1 need";
            if (Math.Abs(Scale("usn_ddg_kidd", 1.0) - StrikeCalib.KScale) > 1e-12) return "scale default " + Scale("usn_ddg_kidd", 1.0);
            // the difficulty 0.8 lowers the need of a Kidd at 0.9
            var zd = StrikeZone.Target(k, s); zd.Scale = 0.8;
            int nd = PathNeed(new[] { zd }, 0.9);
            if (nd != 7) return "difficulty 0.8 need kidd = " + nd + ", want 7";
            // the capped K: K x scale never exceeds S x PCap
            var zc = StrikeZone.Target(30, 31); zc.Scale = 3;
            if (zc.KScaled > 31 * StrikeCalib.PCap + 1e-9) return "K cap";
            // a class table entry acts on its class only
            StrikeCalib.ClassTable["calib_test_x"] = 0.5;
            try
            {
                if (Math.Abs(Scale("calib_test_x", 1.0) - 0.5) > 1e-12) return "class table hit";
                if (Math.Abs(Scale("usn_ddg_kidd", 1.0) - StrikeCalib.KScale) > 1e-12) return "class table leaks";
                if (Math.Abs(Scale("calib_test_x", 0.8) - 0.4) > 1e-12) return "class x difficulty";
            }
            finally { StrikeCalib.ClassTable.Remove("calib_test_x"); }
            if (Math.Abs(Scale(null, 0) - StrikeCalib.KScale) > 1e-12) return "difficulty 0 = unknown = 1";
            if (Scale("a", 50) > StrikeCalib.DifficultyMax * StrikeCalib.KScale + 1e-12) return "difficulty clamp high";
            // the missile as the game's chance sees it: the terminal altitude (the Harpoon D: 7 ft) is lower than the sea skimming one, the chance is lower with it for a SAM that has a minimum altitude
            var sam66 = Sam("usn_rim-66b", 0.8, 50, 80000, 1600, 1.4, 0.45, 1.0);
            var hp50 = new StrikeMissile { CruiseAltFt = 50, SpeedKn = 540, FrontalM2 = 0.0803, MassKg = 540, DefBonus = 0.05 };
            var hp7 = new StrikeMissile { CruiseAltFt = 50, ProfileAltFt = 7, ProfileKn = 540, SpeedKn = 540, FrontalM2 = 0.0803, MassKg = 540, DefBonus = 0.05 };
            double f50 = MissileFactor(new[] { sam66 }, hp50, false), f7 = MissileFactor(new[] { sam66 }, hp7, false);
            if (!(f7 < f50)) return "terminal altitude lowers the factor: " + f7.ToString("0.000", CultureInfo.InvariantCulture) + " vs " + f50.ToString("0.000", CultureInfo.InvariantCulture);
            if (!(f7 > 0.05 && f7 < 3.0)) return "factor range " + f7;
            // clearing with a gain
            bool skip;
            var near = StrikeZone.Near(3.1, 9.5, 0.5, 0.5);
            var c1 = Cleared(near, 0.93, 0.9, 1.0, out skip);
            if (!skip) return "gain 1 skip";
            c1 = Cleared(near, 0.4, 0.9, 1.0, out skip);
            if (skip || Math.Abs(c1.Gone - 0.4) > 1e-9 || Math.Abs(c1.C - 0.5) > 1e-9) return "gain 1 share " + c1.Gone;
            c1 = Cleared(near, 0.93, 0.9, 0.5, out skip);
            if (skip || Math.Abs(c1.Gone - 0.5 * 0.93) > 1e-9) return "gain 0.5 no skip, share " + c1.Gone;
            return null;
        }

        // SAMs of the recon table (damage.ini defaults: speed 1.4, out of altitude 0.5, size 1.0)
        private static StrikeSam Sam(string a, double pk, double lo, double hi, double vmax, double speed, double ooa, double size)
        {
            return new StrikeSam { Ammo = a, Pk = pk, MinAltFt = lo, MaxAltFt = hi, MaxAttackKn = vmax, SpeedMult = speed, OutOfAltPen = ooa, SizeMult = size };
        }

        private static string SelfTestPk()
        {
            var sams = new[]
            {
                Sam("usn_rim-7m", 0.9, 25, 50000, 1000, 1.4, 0.20, 0.5), Sam("usn_rim-66b", 0.8, 50, 80000, 1600, 1.4, 0.45, 1.0),
                Sam("usn_rim-66c", 0.85, 50, 100000, 2000, 0.75, 0.45, 0.85), Sam("usn_rim-67b", 0.85, 50, 120000, 2400, 0.75, 0.45, 0.85),
                Sam("wp_sa-n-9", 0.9, 32, 19685, 1200, 1.4, 0.25, 0.5), Sam("wp_sa-n-4c", 0.85, 15, 13123, 1000, 1.4, 0.2, 1.0),
                Sam("wp_sa-n-6", 0.8, 82, 98425, 2300, 0.75, 0.45, 0.85),
            };
            // asm: alt ft, kn, FrontalArea, Mass, MissileDefenceBonus: Harpoon C, AM-39, YJ-6, YJ-1, AS-6, AS-4
            var asm = new[]
            {
                new[] { 50, 540, 0.0803, 520, 0 }, new[] { 33, 620, 0.1394, 670, 0 }, new[] { 165, 622, 0.6182, 2125, -0.05 },
                new[] { 164, 1350, 0.6009, 1500, 0.05 }, new[] { 60000, 1720, 0.6695, 4500, 0.05 }, new[] { 82000, 2400, 0.8308, 5820, 0.05 },
            };
            double[][] want =   // T-340 section 4.2 (intercept_pk.py)
            {
                new[] { 0.68, 0.63, 0.75, 0.12, 0.05, 0.05 }, new[] { 0.41, 0.21, 0.61, 0.42, 0.30, 0.05 }, new[] { 0.48, 0.24, 0.69, 0.58, 0.54, 0.43 },
                new[] { 0.48, 0.24, 0.69, 0.60, 0.57, 0.52 }, new[] { 0.68, 0.66, 0.81, 0.35, 0.05, 0.05 }, new[] { 0.52, 0.48, 0.59, 0.09, 0.05, 0.05 },
                new[] { 0.20, 0.15, 0.59, 0.51, 0.53, 0.47 },
            };
            for (int i = 0; i < sams.Length; i++)
                for (int j = 0; j < asm.Length; j++)
                {
                    double p = InterceptChance(sams[i], asm[j][0], asm[j][1], asm[j][2], asm[j][3], asm[j][4]);
                    if (Math.Abs(p - want[i][j]) > 0.01) return "pk " + sams[i].Ammo + " x asm" + j + " = " + p.ToString("0.000", CultureInfo.InvariantCulture) + ", want " + want[i][j];
                }
            if (InterceptChance(sams[0], 82000, 2400, 0.8, 5000, 0.05) < InterceptFloor - 1e-12) return "floor";
            // missile factor
            var harpoon = new StrikeMissile { CruiseAltFt = 50, SpeedKn = 540, FrontalM2 = 0.0803, MassKg = 520 };
            if (MissileFactor(null, harpoon, false) != 1 || MissileFactor(new List<StrikeSam>(), harpoon, false) != 1) return "factor without sams";
            double f7 = MissileFactor(new[] { sams[0] }, harpoon, false);
            if (!(f7 > 0.5 && f7 < 1.5)) return "factor rim-7m " + f7;
            var am39 = new StrikeMissile { CruiseAltFt = 33, SpeedKn = 620, FrontalM2 = 0.1394, MassKg = 670 };
            if (!(MissileFactor(new[] { sams[1] }, am39, false) < MissileFactor(new[] { sams[1] }, harpoon, false) + 0.5)) return "factor am39";
            var as6 = new StrikeMissile { CruiseAltFt = 60000, SpeedKn = 1720, FrontalM2 = 0.6695, MassKg = 4500, DefBonus = 0.05 };
            if (MissileFactor(new[] { sams[0] }, as6, true) >= 1) return "factor as6 vs rim-7m < 1";
            return null;
        }

        private static string SelfTestCache()
        {
            string json = "{\"Version\":3,\"Units\":{\"vessels/usn_ddg_kidd\":{\"Roles\":{\"aa_k_subskim\":8.86,\"aa_s_subskim\":27.7,\"aa_k_supskim\":5.2,\"aa_s_supskim\":12.3,\"asuw\":26}},"
                          + "\"vessels/civ_tanker\":{\"Roles\":{\"asuw\":0}},\"aircraft/wp_il-38\":{\"Roles\":{\"aa_k_subskim\":1,\"aa_s_subskim\":1}}}}";
            var d = new Dictionary<string, StrikeAd>();
            string err;
            if (!ParseCache(json, d, out err)) return "parse ok: " + err;
            if (d.Count != 1 || !d.ContainsKey("usn_ddg_kidd")) return "parse units " + d.Count;
            var k = d["usn_ddg_kidd"];
            if (k.Source != "cache" || k.Ksub != 8.86 || k.Ssup != 12.3) return "parse values";
            if (ParseCache("{\"Version\":2,\"Units\":{}}", d, out err) || err != "version") return "version " + err;
            if (ParseCache("{not json", d, out err) || err == null || !err.StartsWith("bad_json", StringComparison.Ordinal)) return "bad json " + err;
            if (ParseCache("{\"Version\":3}", d, out err) || err != "no_units") return "no units " + err;
            var onlySub = FromRoles(new Dictionary<string, double> { { "aa_k_subskim", 4 }, { "aa_s_subskim", 12 } });
            if (onlySub.Source != "cache" || onlySub.Ksup != 4 || onlySub.Ssup != 12) return "sup defaults to sub";
            if (FromRoles(new Dictionary<string, double>()).Source != "none") return "no roles";
            return null;
        }
    }
}
