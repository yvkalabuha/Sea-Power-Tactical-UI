using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VoiceBotBridge
{
    // T-102 / ADR-0038 section 12: the smart-attack state file kept next to the game save (style of AswSave, ADR-0036 section 9). Pure.

    internal sealed class SmartSaveData
    {
        public string AttackId, State;
        public int SubUid;
        public string SubName, SubType;
        public string Ammo;
        public double LaunchT, TorpLat, TorpLon;
        public int TrackId, Side;
        public double PhiDeg, LegNm, LegOdoNm;
        public int Stage;
        public double? TorpDepthFt;
        public List<TrackSample> History = new List<TrackSample>();
        public Dictionary<string, string> Saved = new Dictionary<string, string>();   // ControlMemory field key -> value saved before the attack
        public List<int> AnsweredNeutrals = new List<int>();
        public double MissionT;
        public SavStamp Sav;          // T-198 (ADR-0086): stamp of the game save the file was written for; null = old file
        public WireAmmo AmmoParams;   // parameters of the torpedo, so a load does not depend on the launcher still holding one
    }

    internal static class SmartAttackSave
    {
        public const int MaxHistory = 120;

        public static string Key(string savPath) { return SaveSlotStore.Key(savPath); }

        public static bool Fresh(SmartSaveData d, double missionT, double tolS = 5)
        {
            return d != null && Math.Abs(d.MissionT - missionT) <= tolS;
        }

        /// <summary>Index into <paramref name="live"/>: same ammo, still on the wire, nearest to the saved position; -1 = none.</summary>
        public static int MatchTorpedo(string ammo, double lat, double lon, IList<(int id, string ammo, double lat, double lon, bool onWire)> live)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < live.Count; i++)
            {
                var t = live[i];
                if (!t.onWire || t.ammo != ammo) continue;
                double d = (t.lat - lat) * (t.lat - lat) + (t.lon - lon) * (t.lon - lon);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>At most 120 points, at least 1 s apart; the most recent ones are kept.</summary>
        public static List<TrackSample> Compress(IList<TrackSample> h)
        {
            var r = new List<TrackSample>();
            if (h == null) return r;
            double last = double.NegativeInfinity;
            foreach (var s in h)
            {
                if (s.T - last < 1.0 - 1e-9) continue;
                r.Add(s);
                last = s.T;
            }
            if (r.Count > MaxHistory) r.RemoveRange(0, r.Count - MaxHistory);
            return r;
        }

        // ---- writer ------------------------------------------------------------------------------------------------

        private static string N(double d) { return Json.Num(d); }
        private static string I(int i) { return i.ToString(CultureInfo.InvariantCulture); }

        private static string Hist(TrackSample s)
        {
            return "[" + N(s.T) + "," + N(s.Pos.X) + "," + N(s.Pos.Y) + "," + N(s.VelKn.X) + "," + N(s.VelKn.Y) + "," + (s.DepthFt.HasValue ? N(s.DepthFt.Value) : "null") + "]";
        }

        public static string ToJson(SmartSaveData d)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"format\":1");
            if (d.Sav != null) sb.Append(",\"sav\":").Append(d.Sav.ToJson());
            sb.Append(",\"attack_id\":").Append(Json.Str(d.AttackId));
            sb.Append(",\"state\":").Append(Json.Str(d.State));
            sb.Append(",\"sub\":{\"uid\":").Append(I(d.SubUid)).Append(",\"name\":").Append(Json.Str(d.SubName)).Append(",\"type\":").Append(Json.Str(d.SubType)).Append('}');
            sb.Append(",\"torpedo\":{\"ammo\":").Append(Json.Str(d.Ammo)).Append(",\"launch_t\":").Append(N(d.LaunchT))
              .Append(",\"lat\":").Append(N(d.TorpLat)).Append(",\"lon\":").Append(N(d.TorpLon)).Append('}');
            sb.Append(",\"track_id\":").Append(I(d.TrackId));
            sb.Append(",\"plan\":{\"side\":").Append(I(d.Side)).Append(",\"phi_deg\":").Append(N(d.PhiDeg)).Append(",\"leg_nm\":").Append(N(d.LegNm))
              .Append(",\"stage\":").Append(I(d.Stage)).Append(",\"torp_depth_ft\":").Append(d.TorpDepthFt.HasValue ? N(d.TorpDepthFt.Value) : "null").Append('}');
            sb.Append(",\"leg_odo_nm\":").Append(N(d.LegOdoNm));
            sb.Append(",\"history\":[");
            for (int i = 0; i < d.History.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Hist(d.History[i])); }
            sb.Append(']');
            sb.Append(",\"control_memory\":{");
            bool first = true;
            foreach (var kv in d.Saved) { if (!first) sb.Append(','); first = false; sb.Append(Json.Str(kv.Key)).Append(':').Append(Json.Str(kv.Value)); }
            sb.Append('}');
            sb.Append(",\"answered_neutrals\":[");
            for (int i = 0; i < d.AnsweredNeutrals.Count; i++) { if (i > 0) sb.Append(','); sb.Append(I(d.AnsweredNeutrals[i])); }
            sb.Append(']');
            sb.Append(",\"mission_t\":").Append(N(d.MissionT));
            if (d.AmmoParams != null) sb.Append(",\"ammo_params\":").Append(AmmoJson(d.AmmoParams));
            return sb.Append('}').ToString();
        }

        private static string AmmoJson(WireAmmo a)
        {
            return "{\"file\":" + Json.Str(a.File) + ",\"depth\":" + Json.Bool(a.WireControlsDepth) + ",\"asw\":" + Json.Bool(a.Asw) + ",\"asuw\":" + Json.Bool(a.Asuw) +
                   ",\"speeds\":[" + string.Join(",", Array.ConvertAll(a.SpeedsKn ?? new double[0], N)) + "],\"ranges\":[" + string.Join(",", Array.ConvertAll(a.RangesNm ?? new double[0], N)) + "]" +
                   ",\"break\":" + N(a.WireBreakKn) + ",\"max_on_wire\":" + (double.IsInfinity(a.MaxSpeedOnWireKn) ? "null" : N(a.MaxSpeedOnWireKn)) + ",\"seabed\":" + N(a.SeafloorClearanceFt) +
                   ",\"terminal\":" + N(a.TerminalKn) + ",\"seeker\":" + N(a.SeekerRangeNm) + ",\"fraction\":" + N(a.ActivateFraction) + ",\"fov\":" + N(a.SeekerFovDeg) + ",\"max_depth\":" + N(a.MaxDepthFt) + "}";
        }

        private static double[] Arr(object o)
        {
            var l = o as List<object>;
            if (l == null) return new double[0];
            return l.Select(x => (double)x).ToArray();
        }

        private static WireAmmo AmmoFrom(Dictionary<string, object> o)
        {
            if (o == null) return null;
            object m;
            return new WireAmmo
            {
                File = S(o, "file"), WireGuided = true, WireControlsDepth = o.ContainsKey("depth") && o["depth"] is bool && (bool)o["depth"],
                Asw = o.ContainsKey("asw") && o["asw"] is bool && (bool)o["asw"], Asuw = o.ContainsKey("asuw") && o["asuw"] is bool && (bool)o["asuw"],
                SpeedsKn = Arr(o.ContainsKey("speeds") ? o["speeds"] : null), RangesNm = Arr(o.ContainsKey("ranges") ? o["ranges"] : null),
                WireBreakKn = D(o, "break", 20), MaxSpeedOnWireKn = o.TryGetValue("max_on_wire", out m) && m is double ? (double)m : double.PositiveInfinity,
                SeafloorClearanceFt = D(o, "seabed"), TerminalKn = D(o, "terminal"), SeekerRangeNm = D(o, "seeker"), ActivateFraction = D(o, "fraction"),
                SeekerFovDeg = D(o, "fov"), MaxDepthFt = D(o, "max_depth"),
            };
        }

        // ---- reader ------------------------------------------------------------------------------------------------

        private static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }
        private static List<object> L(object o) { return o as List<object>; }
        private static double D(Dictionary<string, object> d, string k, double def = 0)
        {
            object v; if (d != null && d.TryGetValue(k, out v) && v is double) return (double)v; return def;
        }
        private static double? DN(Dictionary<string, object> d, string k)
        {
            object v; if (d != null && d.TryGetValue(k, out v) && v is double) return (double)v; return null;
        }
        private static string S(Dictionary<string, object> d, string k)
        {
            object v; if (d != null && d.TryGetValue(k, out v)) return v as string; return null;
        }
        private static Dictionary<string, object> Sub(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? O(v) : null;
        }

        public static SmartSaveData Parse(string json, out string err)
        {
            err = null;
            try
            {
                var root = O(Json.Parse(json));
                if (root == null) { err = "not_an_object"; return null; }
                if ((int)D(root, "format", -1) != 1) { err = "unsupported_format"; return null; }
                var sub = Sub(root, "sub"); var torp = Sub(root, "torpedo"); var plan = Sub(root, "plan");
                if (sub == null || torp == null || plan == null) { err = "missing_section"; return null; }
                var d = new SmartSaveData
                {
                    AttackId = S(root, "attack_id"), State = S(root, "state"),
                    SubUid = (int)D(sub, "uid"), SubName = S(sub, "name"), SubType = S(sub, "type"),
                    Ammo = S(torp, "ammo"), LaunchT = D(torp, "launch_t"), TorpLat = D(torp, "lat"), TorpLon = D(torp, "lon"),
                    TrackId = (int)D(root, "track_id"), Side = (int)D(plan, "side"), PhiDeg = D(plan, "phi_deg"), LegNm = D(plan, "leg_nm"),
                    Stage = (int)D(plan, "stage"), TorpDepthFt = DN(plan, "torp_depth_ft"), LegOdoNm = D(root, "leg_odo_nm"), MissionT = D(root, "mission_t"),
                };
                foreach (object ho in L(root.ContainsKey("history") ? root["history"] : null) ?? new List<object>())
                {
                    var a = L(ho);
                    if (a == null || a.Count < 6) { err = "bad_history"; return null; }
                    d.History.Add(new TrackSample
                    {
                        T = (double)a[0], Pos = new SaVec((double)a[1], (double)a[2]), VelKn = new SaVec((double)a[3], (double)a[4]),
                        DepthFt = a[5] is double ? (double?)(double)a[5] : null,
                    });
                }
                d.AmmoParams = AmmoFrom(Sub(root, "ammo_params"));
                { object sv; d.Sav = root.TryGetValue("sav", out sv) ? SavStamp.FromJson(sv) : null; }
                var cm = Sub(root, "control_memory");
                if (cm != null) foreach (var kv in cm) { var sv = kv.Value as string; if (sv != null) d.Saved[kv.Key] = sv; }
                foreach (object n in L(root.ContainsKey("answered_neutrals") ? root["answered_neutrals"] : null) ?? new List<object>())
                    if (n is double) d.AnsweredNeutrals.Add((int)(double)n);
                return d;
            }
            catch (Exception e)
            {
                err = "parse: " + e.Message;
                return null;
            }
        }

        // ---- sample and self test ----------------------------------------------------------------------------------

        private static SmartSaveData SampleData()
        {
            var d = new SmartSaveData
            {
                AttackId = "sa-1", State = "Leg", SubUid = 21, SubName = "Los Angeles", SubType = "SSN-688",
                Ammo = "usn_mk48_mod4_adcap", LaunchT = 1180.5, TorpLat = 35.012, TorpLon = 20.034, TrackId = 12,
                Side = 1, PhiDeg = 60, LegNm = 3, LegOdoNm = 1.25, Stage = 0, TorpDepthFt = 330, MissionT = 1234.5,
            };
            for (int i = 0; i < 5; i++)
                d.History.Add(new TrackSample { T = 1200 + i, Pos = new SaVec(1.5 + 0.01 * i, 9.0), VelKn = new SaVec(8, 3), DepthFt = i % 2 == 0 ? (double?)null : 150 });
            d.AmmoParams = WireAmmo.FromIni("usn_mk48_mod4_adcap", SmartAttackMath.AdcapIni);
            d.Saved["telegraph"] = "3";
            d.Saved["sa_depth"] = "400";
            d.AnsweredNeutrals.Add(7);
            return d;
        }

        /// <summary>Reference file for the pytest side (smarttest save-sample).</summary>
        public static string Sample() { return ToJson(SampleData()); }

        public static string SelfTest()
        {
            if (Key("/A/B.sav") != Key("/a/b.SAV")) return "key case";
            if (Key("/a") == Key("/b")) return "key distinct";
            if (Key("x").Length != 40) return "key length";
            var d = SampleData();
            string j1 = ToJson(d);
            string err;
            var back = Parse(j1, out err);
            if (back == null) return "parse: " + err;
            if (ToJson(back) != j1) return "roundtrip differs";
            if (back.History.Count != 5 || back.Saved["telegraph"] != "3" || back.AnsweredNeutrals[0] != 7 || back.TorpDepthFt != 330) return "roundtrip content";
            if (back.AmmoParams == null || back.AmmoParams.RangesNm[1] != 20 || !back.AmmoParams.Asw || SmartAttackMath.ValidateAmmo(back.AmmoParams) != null) return "ammo params roundtrip";
            if (back.History[0].DepthFt.HasValue || back.History[1].DepthFt != 150) return "history depth null";
            Parse("{bad", out err);
            if (err == null) return "bad json";
            Parse(j1.Replace("\"format\":1", "\"format\":9"), out err);
            if (err != "unsupported_format") return "format check";
            Parse("[1,2]", out err);
            if (err != "not_an_object") return "not an object";
            if (!Fresh(d, 1238.0) || !Fresh(d, 1230.0) || Fresh(d, 1240.0) || Fresh(d, 1000.0)) return "fresh";
            var live = new List<(int id, string ammo, double lat, double lon, bool onWire)>
            {
                (1, "usn_mk48_mod4_adcap", 35.5, 20.5, true),
                (2, "usn_mk48_mod4_adcap", 35.013, 20.035, true),
                (3, "usn_mk48_mod4_adcap", 35.012, 20.034, false),
                (4, "other", 35.012, 20.034, true),
            };
            if (MatchTorpedo("usn_mk48_mod4_adcap", 35.012, 20.034, live) != 1) return "match nearest on wire";
            if (MatchTorpedo("none", 0, 0, live) != -1) return "match none";
            var h = new List<TrackSample>();
            for (int i = 0; i < 400; i++) h.Add(new TrackSample { T = i * 0.5 });
            var c = Compress(h);
            if (c.Count != 120 || c[119].T != 199.0 || c[1].T - c[0].T < 1.0) return "compress " + c.Count;
            if (Compress(null).Count != 0) return "compress null";
            if (Parse(Sample(), out err) == null) return "sample parse";
            return null;
        }
    }
}
