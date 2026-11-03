using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge.Ui
{
    internal struct CiUnits { public bool Km, Kmh, Meters; }

    /// <summary>T-136 / ADR-0045: pure calculations and formatting of the contact_info module. No game, no Unity.</summary>
    internal static class ContactInfoMath
    {
        public const int LayerFirst = 210, LayerLast = 229;
        public static readonly int[] LayerPreferred = { 210, 211, 212, 213, 214, 215, 216 };   // 217-219 reserved for T-137
        /// <summary>T-154 / ADR-0051: ONE map-layer entry for all contact labels (the master switch, ContactInfo.Hotkey); the 7 label layers stay as Show.* / Hotkey.* settings.</summary>
        public static readonly string[] LayerNames = { "VOICEBOT_CONTACT_INFO" };
        public const string BtmHarmonyId = "bananaduck.bettertacmap.patch", NdHarmonyId = "com.seapowermods.noisedisplay", NdAssembly = "NoiseDisplay";
        private const double KmPerNm = 1.852, MPerFt = 0.3048;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- geometry (local flat plane; vx = east, vz = north) ----
        /// <summary>T-352: globe = the beta world (1 deg of longitude = 60 cos(lat) nm), otherwise the flat world of stable (60 nm on both axes).</summary>
        public static void ToLocalNm(double lat0, double lon0, double lat, double lon, bool globe, out double xNm, out double yNm)
        {
            yNm = (lat - lat0) * 60.0;
            xNm = (lon - lon0) * 60.0 * (globe ? Math.Cos(lat0 * Math.PI / 180.0) : 1.0);
        }

        /// <summary>r = target relative to the reference (nm), v = relative velocity (kn). false: |v| &lt; 0.5 kn, opening, or later than maxS.</summary>
        public static bool Cpa(double rxNm, double ryNm, double vxKn, double vyKn, double maxS, out double cpaNm, out double tcpaS)
        {
            cpaNm = double.NaN; tcpaS = double.NaN;
            double v2 = vxKn * vxKn + vyKn * vyKn;
            if (double.IsNaN(v2) || double.IsNaN(rxNm) || double.IsNaN(ryNm) || v2 < 0.25) return false;
            double th = -(rxNm * vxKn + ryNm * vyKn) / v2;
            double ts = th * 3600.0;
            if (!(ts > 0) || ts > maxS) return false;
            double dx = rxNm + vxKn * th, dy = ryNm + vyKn * th;
            cpaNm = Math.Sqrt(dx * dx + dy * dy); tcpaS = ts;
            return true;
        }

        private static bool CpaOne(CiOwn o, CiTrack t, double maxS, bool globe, out double cpa, out double tcpa)
        {
            cpa = double.NaN; tcpa = double.NaN;
            if (o == null || t == null || !t.HasVel) return false;
            double x, y;
            ToLocalNm(o.Lat, o.Lon, t.Lat, t.Lon, globe, out x, out y);
            return Cpa(x, y, t.VxKn - o.VxKn, t.VzKn - o.VzKn, maxS, out cpa, out tcpa);
        }

        public static bool CpaGroup(IList<CiOwn> own, CiTrack t, double maxS, bool globe, out double cpaNm, out double tcpaS, out int refIndex)
        {
            cpaNm = double.NaN; tcpaS = double.NaN; refIndex = -1;
            if (own == null) return false;
            for (int i = 0; i < own.Count; i++)
            {
                double c, ts;
                if (!CpaOne(own[i], t, maxS, globe, out c, out ts)) continue;
                if (refIndex < 0 || c < cpaNm) { cpaNm = c; tcpaS = ts; refIndex = i; }
            }
            return refIndex >= 0;
        }

        public static double TtgOwn(double rangeNm, double speedKn)
        {
            if (double.IsNaN(rangeNm) || double.IsNaN(speedKn) || speedKn < 1.0 || rangeNm < 0) return double.NaN;
            return rangeNm / speedKn * 3600.0;
        }

        /// <summary>Foreign weapon, by its track only: the own unit with the smallest TCPA among those with CPA &lt;= hitNm. -1 = none.</summary>
        public static int TtgForeign(CiTrack weapon, IList<CiOwn> own, double hitNm, double maxS, bool globe, out double ttgS)
        {
            ttgS = double.NaN;
            int best = -1;
            if (own == null) return -1;
            for (int i = 0; i < own.Count; i++)
            {
                double c, ts;
                if (!CpaOne(own[i], weapon, maxS, globe, out c, out ts) || c > hitNm) continue;
                if (best < 0 || ts < ttgS) { best = i; ttgS = ts; }
            }
            return best;
        }

        // ---- depth / layer / age / cavitation / route ----
        public static string LayerSide(bool hasSolution, double depthFt, double layerDepthFt, out double offsetFt)
        {
            offsetFt = double.NaN;
            if (!hasSolution || double.IsNaN(depthFt)) return "nosol";
            if (double.IsNaN(layerDepthFt)) return "none";
            offsetFt = layerDepthFt - depthFt;
            return offsetFt > 0 ? "above" : "below";
        }

        public static double Age(double missionTimeS, double epochS)
        {
            if (double.IsNaN(missionTimeS) || double.IsNaN(epochS)) return double.NaN;
            return Math.Max(0.0, missionTimeS - epochS);
        }

        public static bool IsStale(double ageS, double staleS) { return !double.IsNaN(ageS) && ageS > staleS; }

        public static string CavState(double speedKn, double onsetKn, bool cavitating, double marginKn)
        {
            if (cavitating) return "cav";
            if (double.IsNaN(onsetKn) || double.IsNaN(speedKn)) return null;
            return speedKn >= onsetKn - marginKn ? "near" : "ok";
        }

        /// <summary>wps: {lat, lon} pairs. Sum of legs from the start; NaN when there are no points.</summary>
        public static double RouteNm(double lat0, double lon0, IList<double[]> wps, bool globe)
        {
            if (wps == null || wps.Count == 0) return double.NaN;
            double sum = 0, la = lat0, lo = lon0;
            foreach (var w in wps)
            {
                double x, y;
                ToLocalNm(la, lo, w[0], w[1], globe, out x, out y);
                sum += Math.Sqrt(x * x + y * y);
                la = w[0]; lo = w[1];
            }
            return sum;
        }

        // ---- scheduling and budget (ADR-0045 §4) ----
        public static int PerFrame(int n, double dtS, double periodS)
        {
            if (n <= 0) return 0;
            if (!(periodS > 0) || !(dtS > 0)) return n;
            int k = (int)Math.Ceiling(n * dtS / periodS - 1e-9);
            return Math.Min(n, Math.Max(1, k));
        }

        // ---- formatting ----
        private static string Num(double v, string f) { return v.ToString(f, Inv); }

        private static string Dist(double nm, CiUnits u)
        {
            return u.Km ? Num(nm * KmPerNm, "0.#") + " " + UiText.Get("ci.km") : Num(nm, "0.#") + " " + UiText.Get("ci.nm");
        }

        /// <summary>T-137 D: public wrapper of Dist for other modules' map labels ("25 nm" / "46.3 km").</summary>
        public static string FmtDist(double nm, CiUnits u) { return Dist(nm, u); }

        private static string Speed(double kn, CiUnits u)
        {
            return u.Kmh ? Num(kn * KmPerNm, "0") + " " + UiText.Get("ci.kmh") : Num(kn, "0") + " " + UiText.Get("ov.kn");
        }

        private static double DepthConv(double ft, CiUnits u) { return u.Meters ? ft * MPerFt : ft; }
        private static string DepthUnit(CiUnits u) { return u.Meters ? UiText.Get("ci.m") : UiText.Get("ov.ft"); }
        private static string Len(double ft, CiUnits u) { return Num(Math.Round(DepthConv(ft, u)), "0") + " " + DepthUnit(u); }

        public static string FmtClock(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return "";
            long t = (long)Math.Round(Math.Max(0, s));
            long h = t / 3600, m = t / 60 % 60, sec = t % 60;
            return h > 0 ? h.ToString(Inv) + ":" + m.ToString("00", Inv) + ":" + sec.ToString("00", Inv)
                         : m.ToString(Inv) + ":" + sec.ToString("00", Inv);
        }

        public static string FmtDepth(ContactInfoRow r, bool own, CiUnits u)
        {
            if (r == null) return "";
            if (r.LayerSide == "nosol") return UiText.Get("ci.depth.nosol");
            if (double.IsNaN(r.DepthFt)) return "";
            string v = Len(r.DepthFt, u);
            if (own) return UiText.Fmt("ci.depth.own", v);
            if (!double.IsNaN(r.DepthErrFt) && r.DepthErrFt > 0)
                return UiText.Fmt("ci.depth.est", v, Num(Math.Round(DepthConv(r.DepthErrFt, u)), "0"));
            return UiText.Fmt("ci.depth.est1", v);
        }

        public static string FmtLayer(ContactInfoRow r, CiUnits u)
        {
            if (r == null) return "";
            switch (r.LayerSide)
            {
                case "none": return UiText.Get("ci.layer.none");
                case "above": return double.IsNaN(r.LayerOffsetFt) ? "" : UiText.Fmt("ci.layer.above", "+" + Len(Math.Abs(r.LayerOffsetFt), u));
                case "below": return double.IsNaN(r.LayerOffsetFt) ? "" : UiText.Fmt("ci.layer.below", Len(Math.Abs(r.LayerOffsetFt), u));
                default: return "";
            }
        }

        public static string FmtSpeed(ContactInfoRow r, CiUnits u)
        {
            if (r == null || double.IsNaN(r.SpeedKn)) return "";
            string v = Speed(r.SpeedKn, u);
            return r.SpeedEstimated || r.Stale ? UiText.Fmt("ci.speed.est", v) : v;
        }

        public static string FmtTtg(ContactInfoRow r)
        {
            if (r == null || double.IsNaN(r.TtgS)) return "";
            string c = FmtClock(r.TtgS);
            if (r.Own || r.TtgExact) return UiText.Fmt("ci.ttg.own", c);
            if (string.IsNullOrEmpty(r.TtgTargetName)) return "~" + c;
            return UiText.Fmt("ci.ttg.foe", c, r.TtgTargetName);
        }

        public static string FmtCpa(ContactInfoRow r, CiUnits u)
        {
            if (r == null || double.IsNaN(r.CpaNm) || double.IsNaN(r.TcpaS)) return "";
            string d = Dist(r.CpaNm, u), t = FmtClock(r.TcpaS);
            return string.IsNullOrEmpty(r.CpaRefName) ? UiText.Fmt("ci.cpa", d, t) : UiText.Fmt("ci.cpa.ref", d, t, r.CpaRefName);
        }

        public static string FmtAge(ContactInfoRow r, double ageShowS)
        {
            if (r == null || double.IsNaN(r.AgeS) || r.AgeS <= ageShowS) return "";
            return UiText.Fmt("ci.age", FmtClock(r.AgeS));
        }

        /// <summary>The cavitation part alone ("near cavitation" / "CAV"); "" when ok or unknown.</summary>
        public static string FmtCav(ContactInfoRow r)
        {
            if (r == null) return "";
            if (r.CavState == "cav") return UiText.Get("ci.cav.on");
            if (r.CavState == "near") return UiText.Get("ci.cav.near");
            return "";
        }

        /// <summary>"margin 3 kn" below the cavitation onset (ASW noise list); "" when unknown.</summary>
        public static string FmtMargin(double marginKn)
        {
            return double.IsNaN(marginKn) ? "" : UiText.Fmt("ci.cav.margin", Num(Math.Round(marginKn), "0") + " " + UiText.Get("ov.kn"));
        }

        // Keys of the Overview columns and of the ASW window rows. The code that uses them comes with streams E (Overview, ASW window);
        // listing them here keeps the "no orphan keys" guard honest until then.
        public static readonly string[] OverviewColumnKeys = { "overview.col.dep", "overview.col.lyr", "overview.col.cpa", "overview.col.tcpa", "overview.col.ttg" };
        public static readonly string[] AswKeys = { "asw.ci.contact" };

        public static string FmtNoise(ContactInfoRow r)
        {
            if (r == null) return "";
            string db = double.IsNaN(r.NoiseDb) ? "" : UiText.Fmt("ci.noise", Num(Math.Round(r.NoiseDb), "0"));
            string cav = FmtCav(r);
            if (db.Length == 0) return cav;
            return cav.Length == 0 ? db : db + " · " + cav;
        }

        // ---- own speed + noise line (ADR-0045 Supplement 1, 2026-10-22) ----
        public const uint OwnArgbOk = 0xFFB0F0B0u, OwnArgbNear = 0xFFFFD25Au, OwnArgbCav = 0xFFFF6A6Au;

        /// <summary>One compact line at an own ship or submarine: "[200 ft · ]12 kn · 158 dB[ · margin 1 kn | · CAV]". depthOn: Show.Depth (submarines);
        /// dbOn: the dB part (off when Vessel Noise Display already shows it). "" for tracks, own aircraft and when nothing is known.</summary>
        public static string OwnLine(ContactInfoRow r, bool depthOn, bool dbOn, CiUnits u)
        {
            if (r == null || !r.Own || r.Air) return "";
            var sb = new StringBuilder();
            Action<string> add = x => { if (!string.IsNullOrEmpty(x)) { if (sb.Length > 0) sb.Append(" · "); sb.Append(x); } };
            if (depthOn && !double.IsNaN(r.DepthFt)) add(Len(r.DepthFt, u));
            if (!double.IsNaN(r.SpeedKn)) add(Speed(r.SpeedKn, u));
            if (dbOn && !double.IsNaN(r.NoiseDb)) add(UiText.Fmt("ci.own.db", Num(Math.Round(r.NoiseDb), "0")));
            if (sb.Length == 0) return "";
            if (r.CavState == "cav") add(UiText.Get("ci.cav.on"));
            else if (r.CavState == "near")
            {
                double m = r.CavOnsetKn - r.SpeedKn;
                add(double.IsNaN(m) ? UiText.Get("ci.cav.near") : UiText.Fmt("ci.cav.margin", Speed(Math.Max(0, m), u)));
            }
            return sb.ToString();
        }

        /// <summary>Colour of the own line by the margin to cavitation: green ok/unknown, amber near, red cavitating.</summary>
        public static uint OwnArgb(string cavState)
        {
            return cavState == "cav" ? OwnArgbCav : cavState == "near" ? OwnArgbNear : OwnArgbOk;
        }

        /// <summary>Estimated width of a label in map units (longest line x average glyph width + padding): places a LeftOfIcon label right-aligned.</summary>
        public static double EstWidth(string text, double fontSize)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int best = 0, cur = 0;
            foreach (char c in text) { if (c == '\n') { best = Math.Max(best, cur); cur = 0; } else cur++; }
            best = Math.Max(best, cur);
            return best * fontSize * 0.56 + 6;
        }

        public static string FmtWaypoint(CiWaypoint w, string mode, CiUnits u)
        {
            if (w == null || mode == null || mode == "off" || double.IsNaN(w.RouteNm)) return "";
            if (mode == "dist") return UiText.Fmt("ci.wp.dist", Dist(w.RouteNm, u));
            double ttg = TtgOwn(w.RouteNm, w.SpeedKn);
            if (double.IsNaN(ttg)) return "";
            if (mode == "eta" && !double.IsNaN(w.ClockS))
                return UiText.Fmt("ci.wp.eta", FmtClock((w.ClockS + ttg) % 86400.0));
            if (mode == "eta" || mode == "ttg") return UiText.Fmt("ci.wp.ttg", FmtClock(ttg));
            return "";
        }

        /// <summary>Label lines joined by "\n". cavOnly: the dB part is off because a duplicate mod shows it, but the cavitation line stays.</summary>
        public static string LabelText(ContactInfoRow r, bool[] layerOn, double ageShowS, CiUnits u, bool cavOnly = false)
        {
            if (r == null || layerOn == null || layerOn.Length < 7) return "";
            var sb = new StringBuilder();
            Action<string> add = s => { if (!string.IsNullOrEmpty(s)) { if (sb.Length > 0) sb.Append('\n'); sb.Append(s); } };
            if (layerOn[(int)CiLayer.Depth]) { add(FmtDepth(r, r.Own, u)); add(FmtLayer(r, u)); }
            if (layerOn[(int)CiLayer.Speed]) add(FmtSpeed(r, u));
            if (layerOn[(int)CiLayer.Ttg]) add(FmtTtg(r));
            if (layerOn[(int)CiLayer.Cpa]) add(FmtCpa(r, u));
            if (layerOn[(int)CiLayer.Age]) add(FmtAge(r, ageShowS));
            if (layerOn[(int)CiLayer.Noise]) add(FmtNoise(r));
            else if (cavOnly) add(FmtCav(r));
            return sb.ToString();
        }

        /// <summary>"nm|km , kn|kmh , ft|m". A bad string gives the defaults and an error text.</summary>
        public static CiUnits ParseUnits(string spec, out string error)
        {
            error = null;
            var u = new CiUnits();
            string[] p = (spec ?? "").Split(',');
            if (p.Length != 3) { error = "units: need 3 parts (nm|km,kn|kmh,ft|m)"; return new CiUnits(); }
            string a = p[0].Trim().ToLowerInvariant(), b = p[1].Trim().ToLowerInvariant(), c = p[2].Trim().ToLowerInvariant();
            if ((a != "nm" && a != "km") || (b != "kn" && b != "kmh") || (c != "ft" && c != "m"))
            {
                error = "units: bad value in '" + spec + "'";
                return new CiUnits();
            }
            u.Km = a == "km"; u.Kmh = b == "kmh"; u.Meters = c == "m";
            return u;
        }

        // ---- layer numbers and duplicates ----
        /// <summary>Preferred number if free in [first..last], else the first free number in the range that is not a preferred one; -1 = none.</summary>
        public static int[] PickLayerNumbers(int[] preferred, ICollection<int> taken, int first, int last)
        {
            var res = new int[preferred.Length];
            var used = new HashSet<int>();
            for (int i = 0; i < preferred.Length; i++)
            {
                int n = preferred[i];
                res[i] = -1;
                if (n >= first && n <= last && (taken == null || !taken.Contains(n)) && used.Add(n)) res[i] = n;
            }
            var pref = new HashSet<int>(preferred);
            for (int i = 0; i < res.Length; i++)
            {
                if (res[i] >= 0) continue;
                for (int n = first; n <= last; n++)
                {
                    if (pref.Contains(n) || used.Contains(n) || (taken != null && taken.Contains(n))) continue;
                    res[i] = n; used.Add(n); break;
                }
            }
            return res;
        }

        /// <summary>Index = CiLayer; true = our label is off because another mod already draws it. "on" forces ours; the waypoint layer is never off.</summary>
        public static bool[] DuplicatesOff(string btmMode, bool btmFound, string ndMode, bool ndFound)
        {
            var off = new bool[7];
            if (btmFound && btmMode != "on")
                off[(int)CiLayer.Depth] = off[(int)CiLayer.Speed] = off[(int)CiLayer.Ttg] = off[(int)CiLayer.Cpa] = off[(int)CiLayer.Age] = true;
            if (ndFound && ndMode != "on") off[(int)CiLayer.Noise] = true;
            return off;
        }

        // ---- self test ----
        public static string SelfTest()
        {
            string err = null;
            Action<bool, string> chk = (c, m) => { if (!c && err == null) err = m; };
            Func<double, double, double, bool> near = (a, b, e) => Math.Abs(a - b) <= e;
            double cpa, tcpa, off; int idx;

            // CPA
            chk(Cpa(5, 0, -10, 0, 7200, out cpa, out tcpa) && near(cpa, 0, 1e-9) && near(tcpa, 1800, 1e-6), "cpa head-on");
            chk(!Cpa(5, 0, 10, 0, 7200, out cpa, out tcpa), "cpa opening");
            chk(!Cpa(0, 5, 10, 0, 7200, out cpa, out tcpa), "cpa r.v = 0");
            chk(!Cpa(5, 0, -0.4, 0, 7200, out cpa, out tcpa), "cpa slow");
            chk(!Cpa(5, 0, -10, 0, 600, out cpa, out tcpa), "cpa beyond max");
            chk(Cpa(5, 1, -10, 0, 7200, out cpa, out tcpa) && near(cpa, 1, 1e-9), "cpa offset");
            double x, y;
            ToLocalNm(10, 20, 11, 20, false, out x, out y);
            chk(near(y, 60, 1e-9) && near(x, 0, 1e-9), "tolocal lat");
            var own = new List<CiOwn> { new CiOwn { Uid = 1, Lat = 0, Lon = 0 }, new CiOwn { Uid = 2, Lat = 0, Lon = 0.0833333, VxKn = 0, VzKn = 0 }, new CiOwn { Uid = 3, Lat = 1, Lon = 0 } };
            var tr = new CiTrack { Id = 9, HasVel = true, Lat = 0.05, Lon = 0.0833333, VxKn = 0, VzKn = -20 };
            chk(CpaGroup(own, tr, 7200, false, out cpa, out tcpa, out idx) && idx == 1 && cpa < 0.01, "cpa group min ref " + idx);
            chk(!CpaGroup(own, new CiTrack { HasVel = false }, 7200, false, out cpa, out tcpa, out idx) && idx == -1, "cpa group no vel");

            // TTG
            chk(near(TtgOwn(10, 600), 60, 1e-9), "ttg own");
            chk(double.IsNaN(TtgOwn(10, 0.5)), "ttg own slow");
            var a = new CiOwn { Uid = 1, Lat = 0, Lon = 0 };
            var b = new CiOwn { Uid = 2, Lat = 0, Lon = 0.05 };  // 3 nm east of A
            var torp = new CiTrack { HasVel = true, Lat = -0.1, Lon = 0.05, VxKn = 0, VzKn = 30 }; // 6 nm south of B, heading north
            double ttg;
            idx = TtgForeign(torp, new List<CiOwn> { a, b }, 1.0, 7200, false, out ttg);
            chk(idx == 1 && near(ttg, 720, 1), "ttg foreign picks B: " + idx + " " + ttg);
            chk(TtgForeign(torp, new List<CiOwn> { a }, 1.0, 7200, false, out ttg) == -1, "ttg foreign none");
            foreach (var f in typeof(CiTrack).GetFields())
                chk(f.Name.IndexOf("Target", StringComparison.OrdinalIgnoreCase) < 0, "CiTrack has a target field: " + f.Name);

            // layer, age, cavitation, route
            chk(LayerSide(true, 300, 420, out off) == "above" && near(off, 120, 1e-9), "layer above");
            chk(LayerSide(true, 500, 420, out off) == "below" && near(off, -80, 1e-9), "layer below");
            chk(LayerSide(true, 500, double.NaN, out off) == "none" && double.IsNaN(off), "layer none");
            chk(LayerSide(false, 500, 420, out off) == "nosol", "layer nosol");
            chk(double.IsNaN(Age(double.NaN, 5)) && double.IsNaN(Age(5, double.NaN)) && Age(5, 9) == 0 && near(Age(100, 40), 60, 1e-9), "age");
            chk(IsStale(31, 30) && !IsStale(30, 30) && !IsStale(double.NaN, 30), "stale");
            chk(CavState(10, 12, false, 1) == "ok" && CavState(11, 12, false, 1) == "near" && CavState(5, 12, true, 1) == "cav" && CavState(5, double.NaN, false, 1) == null, "cav state");
            chk(double.IsNaN(RouteNm(0, 0, new List<double[]>(), false)), "route empty");
            var legs = new List<double[]> { new[] { 1.0, 0.0 }, new[] { 1.0, 1.0 / 60 } };
            chk(near(RouteNm(0, 0, legs, true), 60 + Math.Cos(Math.PI / 180), 1e-6), "route two legs (globe)");   // T-352: the beta expectation
            chk(near(RouteNm(0, 0, legs, false), 61, 1e-6), "route two legs (flat)");
            // T-352 worldflat: 60N, 0.1 deg east of the own ship, going west at 20 kn: 6 nm flat (TCPA 1080 s), 3 nm on the globe (540 s)
            {
                var o60 = new List<CiOwn> { new CiOwn { Uid = 1, Lat = 60, Lon = 10 } };
                var t60 = new CiTrack { Id = 1, HasVel = true, Lat = 60, Lon = 10.1, VxKn = -20, VzKn = 0 };
                chk(CpaGroup(o60, t60, 7200, false, out cpa, out tcpa, out idx) && near(tcpa, 1080, 1.0 / 1080), "worldflat tcpa flat " + tcpa);
                chk(CpaGroup(o60, t60, 7200, true, out cpa, out tcpa, out idx) && near(tcpa, 540, 1.0 / 540), "worldflat tcpa globe " + tcpa);
                ToLocalNm(60, 10, 60, 10.1, false, out x, out y); chk(near(x, 6, 1e-12) && y == 0, "worldflat tolocal flat");
                ToLocalNm(60, 10, 60, 10.1, true, out x, out y); chk(x == (10.1 - 10) * 60.0 * Math.Cos(60 * Math.PI / 180.0), "worldflat tolocal globe == old");
            }

            // scheduling, budget
            chk(PerFrame(100, 1.0 / 60, 1) == 2 && PerFrame(0, 1.0 / 60, 1) == 0 && PerFrame(1, 0.001, 1) == 1 && PerFrame(3, 5, 1) == 3, "per frame");
            var bud = new CiBudget(0.4, 10, 4);
            int fired = 0;
            for (int i = 0; i <= 100; i++) if (bud.Feed(i * 0.1, 0.5, 1.0)) fired++;
            chk(fired == 1 && bud.Doublings == 1 && near(bud.Period, 2, 1e-9), "budget doubling " + fired + " " + bud.Period);
            for (int i = 101; i <= 600; i++) bud.Feed(i * 0.1, 0.5, 1.0);
            chk(near(bud.Period, 4, 1e-9), "budget max " + bud.Period);
            var ok = new CiBudget(0.4, 10, 4);
            for (int i = 0; i <= 300; i++) ok.Feed(i * 0.1, 0.1, 1.0);
            chk(ok.Doublings == 0, "budget within");

            // layer numbers and duplicates
            var free = PickLayerNumbers(LayerPreferred, new HashSet<int>(), LayerFirst, LayerLast);
            chk(free.Length == 7 && free[0] == 210 && free[6] == 216, "pick free");
            var p2 = PickLayerNumbers(LayerPreferred, new HashSet<int> { 212 }, LayerFirst, LayerLast);
            chk(p2[2] == 217 && p2[0] == 210 && p2[3] == 213 && p2[6] == 216, "pick 212 taken: " + p2[2]);
            var all = new HashSet<int>(); for (int n = LayerFirst; n <= LayerLast; n++) all.Add(n);
            var p3 = PickLayerNumbers(LayerPreferred, all, LayerFirst, LayerLast);
            chk(Array.TrueForAll(p3, n => n == -1), "pick none");
            var d = DuplicatesOff("auto", true, "auto", false);
            chk(d[0] && d[1] && d[2] && d[3] && d[4] && !d[5] && !d[6], "dup btm auto");
            d = DuplicatesOff("on", true, "on", true);
            chk(Array.TrueForAll(d, v => !v), "dup on");
            d = DuplicatesOff("auto", false, "auto", true);
            chk(d[5] && !d[0] && !d[4] && !d[6], "dup nd auto");
            d = DuplicatesOff("auto", true, "on", true);
            chk(d[0] && !d[5], "dup nd on");

            // hotkeys: no clash with the other modules' keys
            var taken = new HashSet<string>();
            foreach (string s in new[] { "LeftAlt+O", "LeftAlt+V", "LeftAlt+Y", "LeftAlt+G", "LeftAlt+B", "LeftAlt+LeftShift+B", "LeftAlt+LeftShift+H", "LeftAlt+U", "LeftAlt+J", "LeftAlt+P", "LeftAlt+K", "F7", "F8" }) taken.Add(s);
            for (int i = 1; i <= 9; i++) taken.Add("LeftControl+LeftAlt+Alpha" + i);
            var st = new ContactInfoSettings();
            var mine = new List<string>(st.LayerHotkeys); mine.AddRange(st.Hotkey.Split('|'));
            var seen = new HashSet<string>();
            foreach (string h in mine)
            {
                string e;
                string[] parts = CoverageMath.ParseHotkey(h, out e);
                chk(parts != null, "hotkey parse " + h);
                if (parts == null) continue;
                string joined = string.Join("+", parts);
                chk(!taken.Contains(joined) && seen.Add(joined), "hotkey clash " + joined);
            }
            chk(st.Waypoint == "dist", "waypoint default dist");

            // own speed + noise line: colour and width (texts in TextTest)
            chk(OwnArgb("cav") == OwnArgbCav && OwnArgb("near") == OwnArgbNear && OwnArgb("ok") == OwnArgbOk && OwnArgb(null) == OwnArgbOk, "own argb");
            chk(EstWidth("", 13) == 0 && EstWidth("ab\nabcd", 10) > EstWidth("abcd", 10) - 1e-9 && EstWidth("ab\nabcd", 10) < EstWidth("abcde", 10), "est width");
            chk(OwnLine(null, true, true, new CiUnits()) == "" && OwnLine(new ContactInfoRow { Own = false, SpeedKn = 5 }, true, true, new CiUnits()) == ""
                && OwnLine(new ContactInfoRow { Own = true, Air = true, SpeedKn = 300 }, true, true, new CiUnits()) == "" && OwnLine(new ContactInfoRow { Own = true }, true, true, new CiUnits()) == "", "own line empty cases");
            chk(new ContactInfoSettings().OwnSpeedNoise, "own speed+noise default on");

            // units
            string ue;
            var uu = ParseUnits("km,kmh,m", out ue);
            chk(ue == null && uu.Km && uu.Kmh && uu.Meters, "units km");
            uu = ParseUnits("nm,kn,ft", out ue);
            chk(ue == null && !uu.Km && !uu.Kmh && !uu.Meters, "units default");
            uu = ParseUnits("furlong", out ue);
            chk(ue != null && !uu.Km, "units bad");

            string t = err == null ? TextTest() : null;
            return err ?? t;
        }

        // texts need lang/ (the stand loads it); without the ci.* keys the part is skipped so selfcheck without lang still runs
        private static string TextTest()
        {
            if (!UiText.Has("SeaPowerTacticalUI", "ci.depth.nosol")) return null;
            string err = null;
            Action<bool, string> chk = (c, m) => { if (!c && err == null) err = m; };
            var nm = new CiUnits();
            var track = new ContactInfoRow { Key = 1, DepthFt = 340, DepthErrFt = 60, LayerSide = "above", LayerOffsetFt = 120, SpeedKn = 12, SpeedEstimated = true,
                                             TtgS = 65, TtgTargetName = "Alpha", CpaNm = 0.8, TcpaS = 252, CpaRefName = "Bravo", AgeS = 45, NoiseDb = double.NaN, CavState = "near" };
            using (UiText.Push("en"))
            {
                chk(FmtDepth(track, false, nm) == "~340 ft ±60", "en depth: " + FmtDepth(track, false, nm));
                chk(FmtDepth(new ContactInfoRow { LayerSide = "nosol" }, false, nm) == "Depth: no solution", "en nosol");
                chk(FmtDepth(new ContactInfoRow { DepthFt = 90, LayerSide = "none" }, true, nm) == "90 ft", "en own depth");
                chk(FmtLayer(track, nm) == "+120 ft above layer", "en layer: " + FmtLayer(track, nm));
                chk(FmtLayer(new ContactInfoRow { LayerSide = "none" }, nm) == "no layer", "en no layer");
                chk(FmtSpeed(track, nm) == "~12 kn", "en speed: " + FmtSpeed(track, nm));
                chk(FmtSpeed(new ContactInfoRow { SpeedKn = 12, Stale = true }, nm) == "~12 kn", "en stale speed");
                chk(FmtSpeed(new ContactInfoRow { SpeedKn = 12 }, nm) == "12 kn", "en exact speed");
                chk(FmtTtg(track) == "~1:05 → Alpha", "en ttg foe: " + FmtTtg(track));
                chk(FmtTtg(new ContactInfoRow { TtgS = 65, TtgExact = true }) == "TTG 1:05", "en ttg own");
                chk(FmtCpa(track, nm) == "CPA 0.8 nm in 4:12 (Bravo)", "en cpa: " + FmtCpa(track, nm));
                chk(FmtAge(track, 15) == "seen 0:45 ago" && FmtAge(track, 60) == "", "en age: " + FmtAge(track, 15));
                var noisy = new ContactInfoRow { NoiseDb = 118, CavState = "cav" };
                chk(FmtNoise(noisy) == "118 dB (game model) · CAV", "en noise: " + FmtNoise(noisy));
                var on = new[] { true, true, true, true, true, true, true };
                chk(LabelText(track, on, 15, nm).Split('\n').Length == 7, "label lines: " + LabelText(track, on, 15, nm).Replace('\n', '|'));
                var offNoise = new[] { true, true, true, true, true, false, true };
                chk(LabelText(noisy, offNoise, 15, nm, true) == "CAV" && LabelText(noisy, offNoise, 15, nm) == "", "label cav only");
                var ship = new ContactInfoRow { Own = true, SpeedKn = 12.2, NoiseDb = 157.6, CavOnsetKn = 20, CavState = "ok" };
                chk(OwnLine(ship, true, true, nm) == "12 kn · 158 dB", "en own ship: " + OwnLine(ship, true, true, nm));
                chk(OwnLine(ship, true, false, nm) == "12 kn", "en own ship without dB: " + OwnLine(ship, true, false, nm));
                var sub = new ContactInfoRow { Own = true, DepthFt = 200, SpeedKn = 11.5, NoiseDb = 118, CavOnsetKn = 12, CavState = "near" };
                chk(OwnLine(sub, true, true, nm) == "200 ft · 12 kn · 118 dB · margin 1 kn", "en own sub near: " + OwnLine(sub, true, true, nm));
                chk(OwnLine(sub, false, true, nm) == "12 kn · 118 dB · margin 1 kn", "en own sub no depth: " + OwnLine(sub, false, true, nm));
                var cav = new ContactInfoRow { Own = true, SpeedKn = 25, NoiseDb = 140, Cavitating = true, CavState = "cav" };
                chk(OwnLine(cav, true, true, nm) == "25 kn · 140 dB · CAV", "en own cav: " + OwnLine(cav, true, true, nm));
                chk(OwnLine(new ContactInfoRow { Own = true, SpeedKn = 5, CavState = "near" }, true, true, nm) == "5 kn · near cavitation", "en own near without onset");
                var w = new CiWaypoint { RouteNm = 12.3, SpeedKn = 10, ClockS = 3600 };
                chk(FmtWaypoint(w, "dist", nm) == "to WP 12.3 nm", "en wp dist: " + FmtWaypoint(w, "dist", nm));
                chk(FmtWaypoint(w, "ttg", nm) == "WP in 1:13:48", "en wp ttg: " + FmtWaypoint(w, "ttg", nm));
                chk(FmtWaypoint(w, "eta", nm) == "ETA 2:13:48", "en wp eta: " + FmtWaypoint(w, "eta", nm));
                chk(FmtWaypoint(w, "off", nm) == "", "wp off");
                var km = ParseUnits("km,kmh,m", out string e);
                chk(FmtCpa(track, km).StartsWith("CPA 1.5 km"), "en km: " + FmtCpa(track, km));
                chk(FmtDepth(track, false, km) == "~104 m ±18", "en meters: " + FmtDepth(track, false, km));
            }
            using (UiText.Push("ru"))
            {
                chk(FmtLayer(track, nm) == "+120 \u0444\u0442 \u043d\u0430\u0434 \u0441\u043b\u043e\u0435\u043c", "ru layer: " + FmtLayer(track, nm));
                chk(FmtDepth(new ContactInfoRow { LayerSide = "nosol" }, false, nm) == "\u0413\u043b\u0443\u0431\u0438\u043d\u0430: \u043d\u0435\u0442 \u0440\u0435\u0448\u0435\u043d\u0438\u044f", "ru nosol");
                chk(FmtNoise(new ContactInfoRow { NoiseDb = 118, CavState = "cav" }).EndsWith("\u041a\u0410\u0412"), "ru cav");
                chk(FmtTtg(new ContactInfoRow { TtgS = 65, TtgExact = true }) == "\u0434\u043e \u0446\u0435\u043b\u0438 1:05", "ru ttg own");
                chk(OwnLine(new ContactInfoRow { Own = true, SpeedKn = 12, NoiseDb = 158, CavOnsetKn = 13, CavState = "near" }, true, true, nm) == "12 \u0443\u0437 · 158 \u0434\u0411 · \u0437\u0430\u043f\u0430\u0441 1 \u0443\u0437", "ru own line");
            }
            return err;
        }
    }

    /// <summary>Period backoff: average of the samples of the last window &gt; budgetMs -> period x2 (up to maxPeriodS), once per doubling.</summary>
    internal sealed class CiBudget
    {
        private readonly double _budgetMs, _windowS, _maxPeriodS;
        private double _period, _start = double.NaN, _sum;
        private int _count;
        public CiBudget(double budgetMs, double windowS, double maxPeriodS) { _budgetMs = budgetMs; _windowS = windowS; _maxPeriodS = maxPeriodS; }
        public double Period { get { return _period; } }
        public int Doublings { get; private set; }

        public bool Feed(double realTimeS, double ms, double basePeriodS)
        {
            if (_period <= 0) _period = basePeriodS;
            if (double.IsNaN(_start)) _start = realTimeS;
            _sum += ms; _count++;
            if (realTimeS - _start < _windowS) return false;
            double avg = _count > 0 ? _sum / _count : 0;
            _start = realTimeS; _sum = 0; _count = 0;
            if (avg <= _budgetMs || _period >= _maxPeriodS) return false;
            _period = Math.Min(_period * 2, _maxPeriodS);
            Doublings++;
            return true;
        }
    }
}
