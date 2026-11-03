using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    // T-341 phase B (ADR-0132 section 8, ADR-0086 pattern): the strike groups kept next to the game save. Groups, members, the target, the wait point; NOT the plan
    // (a plan is a promise about the moment it was made: after a load the group comes back "stale, loaded"). Pure: no Unity.
    internal sealed class StrikeSaveGroup
    {
        public string Name, Mode = "single";
        public int Seq, MainId;
        public List<int> Members = new List<int>(), Ships = new List<int>();
        public bool HasWait;
        public double WaitLat, WaitLon;
        // phase D: the weapon status before the mod put the member on hold (review of C), the mode of the group, the jammers the mod had switched on
        public string Goal = "sink";   // T-342: breach | sink (absent in an older file = sink)
        public string Roe;
        public Dictionary<int, string> RoePrev = new Dictionary<int, string>();
        public List<int> EwOn = new List<int>();
        public bool AutoWas, ClearWas;   // T-342 C: the auto / the clearing were on at the save (only for the message after the load; the state itself is not restored, ADR-0133 section 8)
    }

    internal sealed class StrikeSaveData
    {
        public int Version = StrikeSave.Version;
        public double MissionT;
        public SavStamp Sav;
        public List<StrikeSaveGroup> Groups = new List<StrikeSaveGroup>();
    }

    internal static class StrikeSave
    {
        public const int Version = 1;

        public static string ToJson(StrikeSaveData d)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"version\":").Append(d.Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mission_t\":").Append(Json.Num(d.MissionT));
            if (d.Sav != null) sb.Append(",\"sav\":").Append(d.Sav.ToJson());
            sb.Append(",\"groups\":[");
            for (int i = 0; i < d.Groups.Count; i++)
            {
                var g = d.Groups[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Json.Str(g.Name)).Append(",\"seq\":").Append(g.Seq.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"members\":").Append(Ints(g.Members));
                sb.Append(",\"goal\":").Append(Json.Str(g.Goal == "breach" ? "breach" : "sink"));
                sb.Append(",\"mode\":").Append(Json.Str(g.Mode)).Append(",\"main\":").Append(g.MainId.ToString(CultureInfo.InvariantCulture)).Append(",\"ships\":").Append(Ints(g.Ships));
                if (g.HasWait) sb.Append(",\"wait\":[").Append(Json.Num(g.WaitLat)).Append(',').Append(Json.Num(g.WaitLon)).Append(']');
                if (g.Roe != null) sb.Append(",\"roe\":").Append(Json.Str(g.Roe));
                if (g.RoePrev.Count > 0)
                {
                    sb.Append(",\"roe_prev\":{");
                    bool first = true;
                    foreach (var kv in g.RoePrev) { if (!first) sb.Append(','); first = false; sb.Append(Json.Str(kv.Key.ToString(CultureInfo.InvariantCulture))).Append(':').Append(Json.Str(kv.Value)); }
                    sb.Append('}');
                }
                if (g.EwOn.Count > 0) sb.Append(",\"ew_on\":").Append(Ints(g.EwOn));
                if (g.AutoWas) sb.Append(",\"auto_was\":true");
                if (g.ClearWas) sb.Append(",\"clear_was\":true");
                sb.Append('}');
            }
            return sb.Append("]}").ToString();
        }

        private static string Ints(List<int> l)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(l[i].ToString(CultureInfo.InvariantCulture)); }
            return sb.Append(']').ToString();
        }

        private static List<int> ReadInts(object o)
        {
            var res = new List<int>();
            var l = o as List<object>;
            if (l != null) foreach (object x in l) if (x is double) res.Add((int)(double)x);
            return res;
        }

        /// <summary>Null + err on a bad file or an unsupported version.</summary>
        public static StrikeSaveData Parse(string json, out string err)
        {
            err = null;
            try
            {
                var root = Json.Parse(json) as Dictionary<string, object>;
                if (root == null) { err = "not_an_object"; return null; }
                object v;
                if (!root.TryGetValue("version", out v) || !(v is double) || (int)(double)v != Version) { err = "unsupported_version"; return null; }
                var d = new StrikeSaveData();
                if (root.TryGetValue("mission_t", out v) && v is double) d.MissionT = (double)v;
                d.Sav = root.TryGetValue("sav", out v) ? SavStamp.FromJson(v) : null;
                var groups = root.TryGetValue("groups", out v) ? v as List<object> : null;
                foreach (object o in groups ?? new List<object>())
                {
                    var m = o as Dictionary<string, object>;
                    object x;
                    if (m == null || !m.TryGetValue("name", out x) || !(x is string) || ((string)x).Length == 0) continue;
                    var g = new StrikeSaveGroup { Name = (string)x };
                    if (m.TryGetValue("seq", out x) && x is double) g.Seq = (int)(double)x;
                    g.Members = ReadInts(m.TryGetValue("members", out x) ? x : null);
                    if (m.TryGetValue("mode", out x) && x is string) g.Mode = (string)x == "spread" ? "spread" : "single";
                    if (m.TryGetValue("goal", out x) && x is string) g.Goal = (string)x == "breach" ? "breach" : "sink";
                    if (m.TryGetValue("main", out x) && x is double) g.MainId = (int)(double)x;
                    g.Ships = ReadInts(m.TryGetValue("ships", out x) ? x : null);
                    var w = m.TryGetValue("wait", out x) ? x as List<object> : null;
                    if (w != null && w.Count == 2 && w[0] is double && w[1] is double) { g.HasWait = true; g.WaitLat = (double)w[0]; g.WaitLon = (double)w[1]; }
                    if (m.TryGetValue("roe", out x) && x is string) g.Roe = (string)x;
                    var rp = m.TryGetValue("roe_prev", out x) ? x as Dictionary<string, object> : null;
                    if (rp != null) foreach (var kv in rp) { int u; if (kv.Value is string && int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out u)) g.RoePrev[u] = (string)kv.Value; }
                    g.EwOn = ReadInts(m.TryGetValue("ew_on", out x) ? x : null);
                    g.AutoWas = m.TryGetValue("auto_was", out x) && x is bool && (bool)x;
                    g.ClearWas = m.TryGetValue("clear_was", out x) && x is bool && (bool)x;
                    d.Groups.Add(g);
                }
                return d;
            }
            catch (Exception e) { err = "parse: " + e.Message; return null; }
        }

        public static string SelfTest()
        {
            var d = new StrikeSaveData { MissionT = 1234.5 };
            d.Groups.Add(new StrikeSaveGroup { Name = "Удар-1", Seq = 3, Members = { 1, 2 }, Mode = "spread", MainId = 100, Ships = { 100, 101 }, HasWait = true, WaitLat = 39.25, WaitLon = 10.5 });
            d.Groups[0].Roe = "hold"; d.Groups[0].RoePrev[1] = "free"; d.Groups[0].EwOn.Add(2);
            d.Groups.Add(new StrikeSaveGroup { Name = "B", Seq = 4 });
            string json = ToJson(d);
            string err;
            var back = Parse(json, out err);
            if (back == null || back.Groups.Count != 2 || back.Groups[0].Name != "Удар-1" || back.Groups[0].Seq != 3 || back.Groups[0].Members.Count != 2 || back.Groups[0].Mode != "spread"
                || back.Groups[0].MainId != 100 || back.Groups[0].Ships.Count != 2 || !back.Groups[0].HasWait || back.Groups[0].WaitLat != 39.25 || back.Groups[1].HasWait || back.MissionT != 1234.5) return "round trip " + err;
            if (back.Groups[0].Roe != "hold" || back.Groups[0].RoePrev.Count != 1 || back.Groups[0].RoePrev[1] != "free" || back.Groups[0].EwOn.Count != 1 || back.Groups[0].EwOn[0] != 2 || back.Groups[1].Roe != null || back.Groups[1].RoePrev.Count != 0) return "phase D fields";
            if (json.Contains("variants") || json.Contains("timeline") || json.Contains("plan") || json.Contains("auto_was") || json.Contains("clear_was")) return "the plan is not saved, the flags only when true";
            d.Groups[1].AutoWas = true; d.Groups[1].ClearWas = true;
            var back2 = Parse(ToJson(d), out err);
            if (back2 == null || !back2.Groups[1].AutoWas || !back2.Groups[1].ClearWas || back2.Groups[0].AutoWas || back2.Groups[0].ClearWas) return "auto_was / clear_was round trip";
            d.Groups[1].AutoWas = false; d.Groups[1].ClearWas = false;
            d.Groups[0].Goal = "breach";
            json = ToJson(d); back = Parse(json, out err);
            if (back == null || back.Groups[0].Goal != "breach" || back.Groups[1].Goal != "sink" || !json.Contains("\"goal\":\"breach\"")) return "goal round trip";
            var noGoal = Parse("{\"version\":1,\"groups\":[{\"name\":\"G\"}]}", out err);
            if (noGoal == null || noGoal.Groups[0].Goal != "sink") return "old file without goal = sink";
            if (Parse("{broken", out err) != null || err == null) return "broken json";
            if (Parse("{\"version\":99,\"groups\":[]}", out err) != null || err != "unsupported_version") return "version";
            if (Parse("[1]", out err) != null || err != "not_an_object") return "not an object";
            var odd = Parse("{\"version\":1,\"groups\":[{\"seq\":1},{\"name\":\"\"},{\"name\":\"X\",\"members\":[1,\"a\",2],\"mode\":\"wide\"}]}", out err);
            if (odd == null || odd.Groups.Count != 1 || odd.Groups[0].Members.Count != 2 || odd.Groups[0].Mode != "single") return "odd file";
            return null;
        }
    }
}
