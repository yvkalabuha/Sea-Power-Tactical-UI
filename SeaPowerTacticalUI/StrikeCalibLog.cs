using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>T-341 phase E1: the line "strike calib" that the calibration script reads (tools/bench/T-341-strike-calib/strike_calib.py). Pure formatting.</summary>
    internal static class StrikeCalibLog
    {
        private static string F3(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string I(int i) { return i.ToString(CultureInfo.InvariantCulture); }
        private static string Clean(string s) { return string.IsNullOrEmpty(s) ? "-" : s.Replace(' ', '_').Replace(';', '_').Replace('/', '_'); }

        /// <summary>One line per target of the launched variant: the inputs of the probability model (K before the scale, the missile factor f and the scale are separate).</summary>
        public static string Line(int planId, string group, int wave, StrikeTargetPlan t, string ammo, bool sup, double diff, string calibId, bool ew, int clearedBy, double now = 0)
        {
            var sb = new StringBuilder("[Strike] strike calib");
            sb.Append(" t=").Append(now.ToString("0.0", CultureInfo.InvariantCulture)).Append(" plan=").Append(I(planId)).Append(" grp=").Append(Clean(group)).Append(" wave=").Append(I(wave));
            sb.Append(" tgt=").Append(I(t.TrackId)).Append(" cls=").Append(Clean(t.ClassIni)).Append(" ammo=").Append(Clean(ammo ?? t.Ammo));
            sb.Append(" sup=").Append(sup ? "1" : "0").Append(" n=").Append(I(t.N)).Append(" p=").Append(F3(t.P)).Append(" sig=").Append(F3(Sigma(t)));
            sb.Append(" diff=").Append(F3(diff)).Append(" calib=").Append(Clean(calibId)).Append(" ew=").Append(ew ? "1" : "0");
            sb.Append(" clr=").Append(clearedBy >= 0 ? I(clearedBy) : "-").Append(" zones=");
            bool first = true;
            if (t.Zones != null)
                foreach (var z in t.Zones)
                {
                    if (!first) sb.Append(';');
                    first = false;
                    double f = z.F > 0 ? z.F : 1, sc = z.Scale > 0 ? z.Scale : 1;
                    sb.Append(I(z.TrackId)).Append('/').Append(Clean(z.ClassIni)).Append('/').Append(F3(z.K / f)).Append('/').Append(F3(z.S)).Append('/').Append(F3(f)).Append('/')
                      .Append(F3(z.IsTarget ? 1 : z.W)).Append('/').Append(F3(z.IsTarget ? 1 : z.C)).Append('/').Append(F3(sc)).Append('/').Append(z.IsTarget ? "1" : "0")
                      .Append('/').Append(z.IsCleared && !z.IsTarget ? F3(1 - Math.Min(1, Math.Max(0, z.Gone))) : "-");
                }
            if (first) sb.Append('-');
            return sb.ToString();
        }

        /// <summary>The survival of one missile along the zones (the sigma of the model; P = 1 - (1 - sigma)^N).</summary>
        public static double Sigma(StrikeTargetPlan t)
        {
            if (t == null || t.N <= 0) return 0;
            double p = t.P;
            return p >= 1 ? 1 : 1 - Math.Pow(1 - p, 1.0 / t.N);
        }

        public static string SelfTest()
        {
            var t = new StrikeTargetPlan { TrackId = 7005, N = 12, P = 0.93, ClassIni = "wp_skr_krivak1", Ammo = "usn_agm-84d" };
            t.Zones.Add(new StrikeZone { TrackId = 7004, ClassIni = "wp_em_sovremenny", K = 14.787, S = 22.658, F = 0.9, W = 0.5, C = 0.5, Scale = 0.8 });
            t.Zones.Add(new StrikeZone { TrackId = 7005, ClassIni = "wp_skr_krivak1", K = 1.2, S = 8, F = 1, W = 1, C = 1, Scale = 0.8, IsTarget = true });
            string line = Line(3, "Strike 1", 1, t, null, false, 0.8, "estimate", true, -1, 1000.04);
            string want = "[Strike] strike calib t=1000.0 plan=3 grp=Strike_1 wave=1 tgt=7005 cls=wp_skr_krivak1 ammo=usn_agm-84d sup=0 n=12 p=0.93 sig=0.199 diff=0.8 calib=estimate ew=1 clr=- "
                          + "zones=7004/wp_em_sovremenny/16.43/22.658/0.9/0.5/0.5/0.8/0/-;7005/wp_skr_krivak1/1.2/8/1/1/1/0.8/1/-";
            if (line != want) return "line:\n" + line + "\nwant:\n" + want;
            // a cleared neighbour: the share of its air defence that is left is the 10th field (0 = skipped)
            var c = new StrikeTargetPlan { TrackId = 2, N = 6, P = 0.9, ClassIni = "usn_ddg_kidd" };
            c.Zones.Add(new StrikeZone { TrackId = 3, ClassIni = "usn_ffg_oliver_hazard_perry", K = 3.1, S = 9.5, F = 1, W = 0.6, C = 0.5, Scale = 1, IsCleared = true, Gone = 1 });
            c.Zones.Add(new StrikeZone { TrackId = 4, ClassIni = "x", K = 3.1, S = 9.5, F = 1, W = 0.6, C = 0.5, Scale = 1, IsCleared = true, Gone = 0.35 });
            c.Zones.Add(StrikeZone.Target(8.86, 27.7));
            string cl = Line(2, "g", 1, c, "a", false, 1, "estimate", false, 3, 5);
            if (!cl.Contains("zones=3/usn_ffg_oliver_hazard_perry/3.1/9.5/1/0.6/0.5/1/0/0;4/x/3.1/9.5/1/0.6/0.5/1/0/0.65;0/-/8.86/27.7/1/1/1/1/1/-")) return "cleared zones: " + cl;
            if (!Line(1, "g", 2, new StrikeTargetPlan { TrackId = 1, N = 1, P = 0.5 }, "a", true, 1, "x", false, 5, 7).EndsWith("clr=5 zones=-", StringComparison.Ordinal)) return "no zones / clr";
            return null;
        }
    }
}
