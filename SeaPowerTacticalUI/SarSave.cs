using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    // T-109 / ADR-0039 section 10: section "sar" of the ASW save (AswSave calls it in wave 2).
    // Keys: groups[], ships[], memory[], rescued_by_mod. Claims, "unreachable" marks, probe cache and routes are NOT saved.
    internal sealed class SarGroupOrderDto { public string Group, AreaKind = "all", OnComplete = "rtb"; public double[] Rect; public double Lat, Lon, RadiusNm; }
    internal sealed class SarShipDto { public int Uid; public string Name, Type, OnComplete = "restore"; public double RadiusNm; }   // RadiusNm 0 = all known
    internal sealed class SarMemDto { public int Uid; public string Name, Telegraph, Depth, Formation; public bool? RaftPickup; public List<double[]> Route; }
    internal sealed class SarSaveData
    {
        public List<SarGroupOrderDto> Groups = new List<SarGroupOrderDto>();
        public List<SarShipDto> Ships = new List<SarShipDto>();
        public List<SarMemDto> Memory = new List<SarMemDto>();
        public int RescuedByMod;
    }

    internal static class SarSave
    {
        private static string N(double d) { return Json.Num(d); }

        private static string Arr<T>(IList<T> l, Func<T, string> f)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(f(l[i])); }
            return sb.Append(']').ToString();
        }

        private static string Nums(IList<double> a)
        {
            return Arr(a, x => N(x));
        }

        public static string ToJson(SarSaveData d)
        {
            if (d == null) d = new SarSaveData();
            var sb = new StringBuilder("{");
            sb.Append("\"groups\":").Append(Arr(d.Groups, g =>
            {
                var s = "{\"group\":" + Json.Str(g.Group) + ",\"area\":" + Json.Str(g.AreaKind) + ",\"on_complete\":" + Json.Str(g.OnComplete);
                if (g.Rect != null) s += ",\"rect\":" + Nums(g.Rect);
                if (g.AreaKind == "circle") s += ",\"lat\":" + N(g.Lat) + ",\"lon\":" + N(g.Lon) + ",\"radius_nm\":" + N(g.RadiusNm);
                return s + "}";
            }));
            sb.Append(",\"ships\":").Append(Arr(d.Ships, s =>
                "{\"uid\":" + s.Uid + ",\"name\":" + Json.Str(s.Name) + ",\"type\":" + Json.Str(s.Type) +
                ",\"radius_nm\":" + N(s.RadiusNm) + ",\"on_complete\":" + Json.Str(s.OnComplete) + "}"));
            sb.Append(",\"memory\":").Append(Arr(d.Memory, m =>
            {
                var s = "{\"uid\":" + m.Uid + ",\"name\":" + Json.Str(m.Name);
                if (m.Telegraph != null) s += ",\"telegraph\":" + Json.Str(m.Telegraph);
                if (m.Depth != null) s += ",\"depth\":" + Json.Str(m.Depth);
                if (m.Formation != null) s += ",\"formation\":" + Json.Str(m.Formation);
                if (m.RaftPickup.HasValue) s += ",\"raft_pickup\":" + Json.Bool(m.RaftPickup.Value);
                if (m.Route != null) s += ",\"route\":" + Arr(m.Route, Nums);
                return s + "}";
            }));
            sb.Append(",\"rescued_by_mod\":").Append(d.RescuedByMod).Append('}');
            return sb.ToString();
        }

        private static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }
        private static List<object> L(object o) { return o as List<object>; }

        private static double D(Dictionary<string, object> d, string k, double def = 0)
        {
            object v;
            return d.TryGetValue(k, out v) && v is double ? (double)v : def;
        }

        private static string S(Dictionary<string, object> d, string k, string def = null)
        {
            object v;
            return d.TryGetValue(k, out v) && v is string ? (string)v : def;
        }

        private static double[] DoubleList(object o)
        {
            var l = L(o);
            if (l == null) return null;
            var r = new List<double>();
            foreach (object x in l) { if (!(x is double)) return null; r.Add((double)x); }
            return r.ToArray();
        }

        /// <summary>Never throws; null/absent/malformed parts give empty or skipped entries.</summary>
        public static SarSaveData Parse(Dictionary<string, object> section)
        {
            var d = new SarSaveData();
            if (section == null) return d;
            try
            {
                var gl = L(section.ContainsKey("groups") ? section["groups"] : null);
                if (gl != null)
                    foreach (object o in gl)
                    {
                        var j = O(o); if (j == null) continue;
                        string name = S(j, "group"); if (name == null) continue;
                        var g = new SarGroupOrderDto { Group = name, AreaKind = S(j, "area", "all"), OnComplete = S(j, "on_complete", "rtb") };
                        if (g.AreaKind != "all" && g.AreaKind != "rect" && g.AreaKind != "circle") g.AreaKind = "all";
                        object r;
                        if (j.TryGetValue("rect", out r)) { var a = DoubleList(r); if (a != null && a.Length == 4) g.Rect = a; }
                        if (g.AreaKind == "rect" && g.Rect == null) g.AreaKind = "all";
                        g.Lat = D(j, "lat"); g.Lon = D(j, "lon"); g.RadiusNm = D(j, "radius_nm");
                        if (g.AreaKind == "circle" && g.RadiusNm <= 0) g.AreaKind = "all";
                        d.Groups.Add(g);
                    }
                var sl = L(section.ContainsKey("ships") ? section["ships"] : null);
                if (sl != null)
                    foreach (object o in sl)
                    {
                        var j = O(o); if (j == null || !(j.ContainsKey("uid") && j["uid"] is double)) continue;
                        d.Ships.Add(new SarShipDto { Uid = (int)D(j, "uid"), Name = S(j, "name"), Type = S(j, "type"),
                                                     RadiusNm = D(j, "radius_nm"), OnComplete = S(j, "on_complete", "restore") });
                    }
                var ml = L(section.ContainsKey("memory") ? section["memory"] : null);
                if (ml != null)
                    foreach (object o in ml)
                    {
                        var j = O(o); if (j == null || !(j.ContainsKey("uid") && j["uid"] is double)) continue;
                        var m = new SarMemDto { Uid = (int)D(j, "uid"), Name = S(j, "name"), Telegraph = S(j, "telegraph"),
                                                Depth = S(j, "depth"), Formation = S(j, "formation") };
                        object v;
                        if (j.TryGetValue("raft_pickup", out v) && v is bool) m.RaftPickup = (bool)v;
                        if (j.TryGetValue("route", out v) && L(v) != null)
                        {
                            m.Route = new List<double[]>();
                            foreach (object p in L(v)) { var a = DoubleList(p); if (a != null && a.Length == 2) m.Route.Add(a); }
                        }
                        d.Memory.Add(m);
                    }
                d.RescuedByMod = (int)D(section, "rescued_by_mod");
            }
            catch (Exception) { }
            return d;
        }

        /// <summary>Old saves/states used "sar" for "busy with AutomaticSAR".</summary>
        public static string MapLegacyReason(string reason) { return reason == "sar" ? "ext_sar" : reason; }

        private static SarSaveData SampleData()
        {
            var d = new SarSaveData { RescuedByMod = 11 };
            d.Groups.Add(new SarGroupOrderDto { Group = "SAR-1", AreaKind = "all", OnComplete = "rtb" });
            d.Groups.Add(new SarGroupOrderDto { Group = "SAR-2", AreaKind = "rect", OnComplete = "loiter", Rect = new[] { 35.0, 20.0, 35.2, 20.3 } });
            d.Groups.Add(new SarGroupOrderDto { Group = "SAR-3", AreaKind = "circle", Lat = 35.1, Lon = 20.1, RadiusNm = 15 });
            d.Ships.Add(new SarShipDto { Uid = 17, Name = "Yorktown", Type = "CG-47", RadiusNm = 10, OnComplete = "restore" });
            d.Ships.Add(new SarShipDto { Uid = 18, Name = "Los Angeles", Type = "SSN", RadiusNm = 0, OnComplete = "stop" });
            d.Memory.Add(new SarMemDto { Uid = 17, Name = "Yorktown", Telegraph = "Half", Formation = "Screen", RaftPickup = false,
                                         Route = new List<double[]> { new[] { 35.0, 20.0 }, new[] { 35.1, 20.1 } } });
            d.Memory.Add(new SarMemDto { Uid = 18, Name = "Los Angeles", Depth = "Deep" });
            return d;
        }

        public static string Sample() { return ToJson(SampleData()); }

        public static string SelfTest()
        {
            var d = SampleData();
            string j1 = ToJson(d);
            var sec = Json.Parse(j1) as Dictionary<string, object>;
            if (sec == null) return "json parse";
            var back = Parse(sec);
            if (ToJson(back) != j1) return "roundtrip differs";
            if (back.Groups.Count != 3 || back.Groups[0].AreaKind != "all" || back.Groups[1].AreaKind != "rect" || back.Groups[1].Rect.Length != 4
                || back.Groups[1].OnComplete != "loiter" || back.Groups[2].AreaKind != "circle" || back.Groups[2].RadiusNm != 15) return "groups content";
            if (back.Ships.Count != 2 || back.Ships[0].RadiusNm != 10 || back.Ships[1].OnComplete != "stop") return "ships content";
            if (back.Memory.Count != 2 || back.Memory[0].Route.Count != 2 || back.Memory[0].RaftPickup != false
                || back.Memory[0].Telegraph != "Half" || back.Memory[1].RaftPickup.HasValue || back.Memory[1].Depth != "Deep") return "memory content";
            if (back.RescuedByMod != 11) return "rescued";
            var e = Parse(null);
            if (e == null || e.Groups.Count != 0 || e.Ships.Count != 0 || e.Memory.Count != 0 || e.RescuedByMod != 0) return "null section";
            if (Parse(new Dictionary<string, object>()).Groups.Count != 0) return "empty section";
            var extra = Json.Parse(j1.Replace("\"rescued_by_mod\"", "\"future\":{\"x\":1},\"rescued_by_mod\"")) as Dictionary<string, object>;
            if (ToJson(Parse(extra)) != j1) return "extra fields";
            var junk = Json.Parse("{\"groups\":[1,\"x\",{\"area\":\"rect\"},{\"group\":\"G\",\"area\":\"rect\"}],\"ships\":{},\"memory\":[{\"uid\":\"a\"}],\"rescued_by_mod\":\"z\"}") as Dictionary<string, object>;
            var jd = Parse(junk);
            if (jd.Groups.Count != 1 || jd.Groups[0].AreaKind != "all" || jd.Ships.Count != 0 || jd.Memory.Count != 0 || jd.RescuedByMod != 0) return "junk tolerated";
            if (MapLegacyReason("sar") != "ext_sar" || MapLegacyReason("busy") != "busy" || MapLegacyReason(null) != null) return "legacy reason";
            if (Parse(Json.Parse(Sample()) as Dictionary<string, object>).Groups.Count != 3) return "sample parse";
            return null;
        }
    }
}
