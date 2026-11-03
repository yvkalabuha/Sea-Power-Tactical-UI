using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-095 / ADR-0036 section 8: parsing of the stateful asw_* commands. The pure part (AswCommandsParse) is tested in aswtest;
    // the GameApi part (dispatch + ack JSON) needs the game classes and lives in AswCommandsGame.cs.

    internal static class AswCommandsParse
    {
        internal sealed class GroupCmd { public string Op, Group, NewName, Kind, Err;   // T-346: Kind = null | asw | sar
             public List<int> Uids = new List<int>(); }
        internal sealed class CourseCmd { public string Group, Depth, Err, TargetKind = "ship"; public int TargetUid; public List<string> Tactics = new List<string>(); public double? LeadNm, WidthNm, CorridorX, BarrierX, StandDip, StandCorridor, StandBarrier, BarrierGap; public int? BarrierRows; }  // T-229: BarrierRows 1..5, BarrierGap = the interval of the rows (nm)
        internal sealed class AreaCmd { public string Group, Depth, Pattern, Err; public int TargetUid; public double[] Rect; public double Bearing, DistNm, SizeNm; public List<string> Tactics = new List<string>(); }
        internal sealed class TacticsCmd { public string Group, Err; public List<string> Tactics = new List<string>(); }
        internal sealed class StopCmd { public string Group, Err; public bool All; }
        internal sealed class DepthCmd { public string Group, Mode, Err; }
        internal sealed class LoadoutCmd { public string Group, Type, Loadout, Err; }
        internal sealed class OrderCmd { public string Group, Targets, Err; public int RefUid; public double? RadiusNm; public int? Airborne; }

        private static readonly string[] Tactics = { "dip", "rows", "cross", "mad", "corridor", "barrier", "grid" };   // T-176: rows|cross are old ids (synonyms of corridor|barrier by course, of grid in an area)

        private static string Str(Dictionary<string, object> c, string k)
        {
            object v; return c != null && c.TryGetValue(k, out v) ? v as string : null;
        }

        private static double? Num(Dictionary<string, object> c, string k)
        {
            object v; if (c != null && c.TryGetValue(k, out v) && v is double) return (double)v; return null;
        }

        /// <summary>"tactics": array of known tactic names, duplicates dropped; false on a wrong shape or an unknown name. Missing key = ok, empty list.</summary>
        private static bool ReadTactics(Dictionary<string, object> c, List<string> into)
        {
            object o;
            if (!c.TryGetValue("tactics", out o)) return true;
            var l = o as List<object>;
            if (l == null) return false;
            foreach (object x in l)
            {
                string s = x as string;
                if (s == null || Array.IndexOf(Tactics, s) < 0) return false;
                if (!into.Contains(s)) into.Add(s);
            }
            return true;
        }

        /// <summary>asw_set_tactics {group?, tactics[]}: a (possibly empty) list is required; empty = reserve everybody.</summary>
        public static TacticsCmd ParseSetTactics(Dictionary<string, object> c)
        {
            var r = new TacticsCmd { Group = Str(c, "group") };
            if (!c.ContainsKey("tactics") || !ReadTactics(c, r.Tactics)) r.Err = "bad_args";
            return r;
        }

        public static GroupCmd ParseGroup(Dictionary<string, object> c)
        {
            var r = new GroupCmd { Op = Str(c, "op"), Group = Str(c, "group") };
            if (r.Op != "create" && r.Op != "add" && r.Op != "remove" && r.Op != "disband" && r.Op != "rename") { r.Err = "bad_args"; return r; }
            if (r.Op == "rename")   // T-150: {op:"rename", group:<old>, name:<new>}
            {
                string nn = Str(c, "name");
                if (r.Group == null || nn == null) r.Err = "bad_args"; else r.NewName = nn.Trim();
                return r;
            }
            r.Kind = Str(c, "kind");
            if (r.Kind != null && r.Kind != "asw" && r.Kind != "sar") { r.Err = "bad_args"; return r; }
            object u;
            if (c.TryGetValue("uids", out u))
            {
                var l = u as List<object>;
                if (l == null) { r.Err = "bad_args"; return r; }
                foreach (object o in l) { if (!(o is double)) { r.Err = "bad_args"; return r; } r.Uids.Add((int)(double)o); }
            }
            if ((r.Op == "add" || r.Op == "remove") && (r.Group == null || r.Uids.Count == 0)) r.Err = "bad_args";
            if (r.Op == "disband" && r.Group == null) r.Err = "bad_args";
            return r;
        }

        public static CourseCmd ParseCourse(Dictionary<string, object> c)
        {
            var r = new CourseCmd { Group = Str(c, "group"), Depth = Str(c, "depth") };
            double? t = Num(c, "target_uid"); if (t.HasValue) r.TargetUid = (int)t.Value;
            r.LeadNm = Num(c, "lead_nm"); r.WidthNm = Num(c, "width_nm");
            string kind = Str(c, "target_kind");
            if (kind != null) { if (kind != "ship" && kind != "formation") { r.Err = "bad_args"; return r; } r.TargetKind = kind; }
            r.CorridorX = Num(c, "corridor_x_nm"); r.BarrierX = Num(c, "barrier_x_nm");
            object so;
            if (c.TryGetValue("standoff", out so))
            {
                var sd = so as Dictionary<string, object>;
                if (sd == null) { r.Err = "bad_args"; return r; }
                r.StandDip = Num(sd, "dip"); r.StandCorridor = Num(sd, "corridor"); r.StandBarrier = Num(sd, "barrier");
            }
            double? br = Num(c, "barrier_rows"); r.BarrierGap = Num(c, "barrier_gap_nm");
            if (br.HasValue)
            {
                if (br.Value != Math.Floor(br.Value) || br.Value < 1 || br.Value > 5) { r.Err = "bad_args"; return r; }
                r.BarrierRows = (int)br.Value;
            }
            foreach (double? v in new[] { r.LeadNm, r.WidthNm, r.CorridorX, r.BarrierX, r.StandDip, r.StandCorridor, r.StandBarrier, r.BarrierGap })
                if (v.HasValue && (v.Value <= 0 || v.Value > 200)) { r.Err = "bad_args"; return r; }
            if (r.Depth != null && r.Depth != "auto" && AswMath.ParseDepth(r.Depth) == null) { r.Err = "bad_args"; return r; }
            if (!ReadTactics(c, r.Tactics)) r.Err = "bad_args";
            return r;
        }

        /// <summary>asw_loadout {group?, type, loadout}: loadout is a name or "auto" (the configured list).</summary>
        public static LoadoutCmd ParseLoadout(Dictionary<string, object> c)
        {
            var r = new LoadoutCmd { Group = Str(c, "group"), Type = Str(c, "type"), Loadout = Str(c, "loadout") };
            if (string.IsNullOrWhiteSpace(r.Type) || string.IsNullOrWhiteSpace(r.Loadout)) r.Err = "bad_args";
            else { r.Type = r.Type.Trim(); r.Loadout = r.Loadout.Trim(); }
            return r;
        }

        /// <summary>rect: 4 [lat, lon] pairs or 8 flat numbers; or bearing + dist_nm (+ size_nm) from the target.</summary>
        public static AreaCmd ParseArea(Dictionary<string, object> c)
        {
            var r = new AreaCmd { Group = Str(c, "group"), Depth = Str(c, "depth"), Pattern = Str(c, "pattern") };
            double? t = Num(c, "target_uid"); if (t.HasValue) r.TargetUid = (int)t.Value;
            if (r.Depth != null && r.Depth != "auto" && AswMath.ParseDepth(r.Depth) == null) { r.Err = "bad_args"; return r; }
            if (!ReadTactics(c, r.Tactics)) { r.Err = "bad_args"; return r; }
            if (r.Pattern != null && r.Pattern != "auto" && r.Pattern != "grid" && r.Pattern != "dip" && r.Pattern != "mad" && r.Pattern != "rows" && r.Pattern != "cross" && r.Pattern != "buoys") { r.Err = "bad_args"; return r; }
            // T-176: the area mode has the buoy grid ("grid"; old rows | cross | buoys are synonyms), dipping sonar and MAD. "pattern" is the v3 way to choose one.
            for (int i = 0; i < r.Tactics.Count; i++)
                if (r.Tactics[i] == "cross" || r.Tactics[i] == "rows" || r.Tactics[i] == "corridor" || r.Tactics[i] == "barrier")
                {
                    if (r.Tactics.IndexOf("grid") >= 0 && r.Tactics.IndexOf("grid") != i) { r.Tactics.RemoveAt(i); i--; } else r.Tactics[i] = "grid";
                }
            if (r.Tactics.Count == 0 && r.Pattern != null)
            {
                string pat = AswPatternMath.MigrateArea(r.Pattern);
                if (pat != "auto") r.Tactics.Add(pat);
            }
            object o;
            if (c.TryGetValue("rect", out o))
            {
                var l = o as List<object>;
                var flat = new List<double>();
                if (l == null) { r.Err = "bad_args"; return r; }
                foreach (object x in l)
                {
                    if (x is double) flat.Add((double)x);
                    else if (x is List<object>)
                    {
                        var p = (List<object>)x;
                        if (p.Count != 2 || !(p[0] is double) || !(p[1] is double)) { r.Err = "bad_args"; return r; }
                        flat.Add((double)p[0]); flat.Add((double)p[1]);
                    }
                    else { r.Err = "bad_args"; return r; }
                }
                if (flat.Count != 8) { r.Err = "bad_args"; return r; }
                for (int i = 0; i < 8; i += 2) if (Math.Abs(flat[i]) > 90 || Math.Abs(flat[i + 1]) > 180) { r.Err = "bad_args"; return r; }
                r.Rect = flat.ToArray();
                return r;
            }
            double? b = Num(c, "bearing"), d = Num(c, "dist_nm");
            if (!b.HasValue || !d.HasValue || d.Value <= 0 || d.Value > 200) { r.Err = "bad_args"; return r; }
            r.Bearing = AswMath.Norm360(b.Value); r.DistNm = d.Value;
            double? s = Num(c, "size_nm");
            if (s.HasValue && (s.Value <= 0 || s.Value > 100)) { r.Err = "bad_args"; return r; }
            r.SizeNm = s ?? 0;
            return r;
        }

        public static StopCmd ParseStop(Dictionary<string, object> c)
        {
            var r = new StopCmd { Group = Str(c, "group") };
            object a;
            if (c.TryGetValue("all", out a) && a is bool && (bool)a) r.All = true;
            if (Str(c, "group") == "all") { r.All = true; r.Group = null; }
            if (!r.All && r.Group == null) r.Err = "bad_args";
            return r;
        }

        public static DepthCmd ParseDepth(Dictionary<string, object> c)
        {
            var r = new DepthCmd { Group = Str(c, "group"), Mode = Str(c, "mode") };
            if (r.Mode == null || (r.Mode != "auto" && AswMath.ParseDepth(r.Mode) == null)) r.Err = "bad_args";
            return r;
        }

        /// <summary>T-173 asw_order {group, order:"strike", ref_uid?, radius_nm? (0 = no limit), targets? (hostile_unknown | hostile), airborne? (0..4)}.</summary>
        public static OrderCmd ParseOrder(Dictionary<string, object> c)
        {
            var r = new OrderCmd { Group = Str(c, "group"), Targets = Str(c, "targets") };
            if (r.Group == null || Str(c, "order") != "strike") { r.Err = "bad_args"; return r; }
            double? u = Num(c, "ref_uid"); if (u.HasValue) r.RefUid = (int)u.Value;
            r.RadiusNm = Num(c, "radius_nm");
            if (r.RadiusNm.HasValue && (r.RadiusNm.Value < 0 || r.RadiusNm.Value > 150)) { r.Err = "bad_args"; return r; }
            double? a = Num(c, "airborne");
            if (a.HasValue) { if (a.Value < 0 || a.Value > 4) { r.Err = "bad_args"; return r; } r.Airborne = (int)a.Value; }
            if (r.Targets != null && r.Targets != "hostile_unknown" && r.Targets != "hostile") r.Err = "bad_args";
            return r;
        }

        public static string SelfTest()
        {
            string err = null;
            Func<string, Dictionary<string, object>> P = s => (Dictionary<string, object>)Json.Parse(s);
            var g = ParseGroup(P("{\"op\":\"create\",\"uids\":[1,2]}"));
            if (g.Err != null || g.Uids.Count != 2) err = err ?? "group create";
            if (ParseGroup(P("{\"op\":\"add\",\"uids\":[1]}")).Err != "bad_args") err = err ?? "group add needs group";
            var kg = ParseGroup(P("{\"op\":\"create\",\"kind\":\"asw\",\"uids\":[1]}"));
            if (kg.Err != null || kg.Kind != "asw" || ParseGroup(P("{\"op\":\"create\",\"kind\":\"sar\",\"uids\":[1]}")).Kind != "sar" || ParseGroup(P("{\"op\":\"create\",\"uids\":[1]}")).Kind != null || ParseGroup(P("{\"op\":\"create\",\"kind\":\"cap\",\"uids\":[1]}")).Err != "bad_args") err = err ?? "group kind";
            if (ParseGroup(P("{\"op\":\"zap\"}")).Err != "bad_args") err = err ?? "group bad op";
            if (ParseGroup(P("{\"op\":\"rename\",\"group\":\"ПЛО-1\"}")).Err != "bad_args") err = err ?? "rename needs name";
            var rn = ParseGroup(P("{\"op\":\"rename\",\"group\":\"ПЛО-1\",\"name\":\" Конвой \"}"));
            if (rn.Err != null || rn.NewName != "Конвой" || rn.Group != "ПЛО-1") err = err ?? "rename ok";
            if (ParseGroup(P("{\"op\":\"create\",\"uids\":[\"x\"]}")).Err != "bad_args") err = err ?? "group bad uids";
            var cc = ParseCourse(P("{\"tactics\":[\"rows\",\"dip\",\"rows\"],\"target_uid\":7,\"depth\":\"below\",\"lead_nm\":5}"));
            if (cc.Err != null || cc.Tactics.Count != 2 || cc.TargetUid != 7 || cc.LeadNm != 5) err = err ?? "course ok";
            if (ParseCourse(P("{\"tactics\":[\"zig\"]}")).Err != "bad_args") err = err ?? "course bad tactic";
            if (ParseCourse(P("{\"depth\":\"sideways\"}")).Err != "bad_args") err = err ?? "course bad depth";
            if (ParseCourse(P("{}")).Err != null) err = err ?? "course empty ok";
            var ck = ParseCourse(P("{\"target_uid\":9,\"target_kind\":\"formation\"}"));
            if (ck.Err != null || ck.TargetKind != "formation" || ck.TargetUid != 9 || ParseCourse(P("{}")).TargetKind != "ship") err = err ?? "course target_kind";
            if (ParseCourse(P("{\"target_kind\":\"fleet\"}")).Err != "bad_args") err = err ?? "course bad target_kind";
            var lc = ParseLoadout(P("{\"group\":\"G\",\"type\":\" SH-3H \",\"loadout\":\"ASWKiller\"}"));
            if (lc.Err != null || lc.Type != "SH-3H" || lc.Loadout != "ASWKiller" || lc.Group != "G") err = err ?? "loadout ok";
            if (ParseLoadout(P("{\"type\":\"SH-3H\"}")).Err != "bad_args" || ParseLoadout(P("{\"loadout\":\"x\"}")).Err != "bad_args") err = err ?? "loadout bad";
            var a1 = ParseArea(P("{\"rect\":[[35,20],[35,20.2],[35.1,20.2],[35.1,20]]}"));
            if (a1.Err != null || a1.Rect == null || a1.Rect.Length != 8 || a1.Rect[3] != 20.2) err = err ?? "area rect pairs";
            var a2 = ParseArea(P("{\"rect\":[35,20,35,20.2,35.1,20.2,35.1,20]}"));
            if (a2.Err != null || a2.Rect.Length != 8) err = err ?? "area rect flat";
            if (ParseArea(P("{\"rect\":[[95,20],[35,20.2],[35.1,20.2],[35.1,20]]}")).Err != "bad_args") err = err ?? "area bad lat";
            if (ParseArea(P("{\"rect\":[[1,2]]}")).Err != "bad_args") err = err ?? "area short rect";
            var a3 = ParseArea(P("{\"bearing\":370,\"dist_nm\":15}"));
            if (a3.Err != null || Math.Abs(a3.Bearing - 10) > 1e-9 || a3.DistNm != 15) err = err ?? "area bearing";
            if (ParseArea(P("{\"bearing\":10}")).Err != "bad_args") err = err ?? "area needs dist";
            if (!ParseStop(P("{\"group\":\"all\"}")).All || !ParseStop(P("{\"all\":true}")).All) err = err ?? "stop all";
            if (ParseStop(P("{}")).Err != "bad_args") err = err ?? "stop empty";
            if (ParseDepth(P("{\"group\":\"g\",\"mode\":\"above\"}")).Err != null || ParseDepth(P("{\"mode\":\"up\"}")).Err != "bad_args") err = err ?? "depth";
            var st = ParseSetTactics(P("{\"group\":\"ПЛО-1\",\"tactics\":[\"mad\",\"mad\",\"rows\"]}"));
            if (st.Err != null || st.Group != "ПЛО-1" || st.Tactics.Count != 2 || st.Tactics[0] != "mad") err = err ?? "set_tactics";
            if (ParseSetTactics(P("{\"tactics\":[]}")).Err != null) err = err ?? "set_tactics empty ok";
            if (ParseSetTactics(P("{}")).Err != "bad_args" || ParseSetTactics(P("{\"tactics\":[\"zig\"]}")).Err != "bad_args") err = err ?? "set_tactics bad";
            var ax = ParseArea(P("{\"bearing\":0,\"dist_nm\":9,\"tactics\":[\"cross\",\"rows\"]}"));
            if (ax.Tactics.Count != 1 || ax.Tactics[0] != "grid") err = err ?? "area cross+rows -> grid";
            ax = ParseArea(P("{\"bearing\":0,\"dist_nm\":9,\"tactics\":[\"cross\",\"mad\"]}"));
            if (ax.Tactics.Count != 2 || ax.Tactics[0] != "grid" || ax.Tactics[1] != "mad") err = err ?? "area cross -> grid";
            ax = ParseArea(P("{\"bearing\":0,\"dist_nm\":9,\"pattern\":\"mad\"}"));
            if (ax.Err != null || ax.Tactics.Count != 1 || ax.Tactics[0] != "mad") err = err ?? "area pattern";
            if (ParseArea(P("{\"bearing\":0,\"dist_nm\":9,\"pattern\":\"zig\"}")).Err != "bad_args") err = err ?? "area bad pattern";
            var cn = ParseCourse(P("{\"tactics\":[\"corridor\",\"barrier\"],\"corridor_x_nm\":30,\"barrier_x_nm\":50,\"standoff\":{\"dip\":40,\"barrier\":60},\"depth\":\"auto\"}"));
            if (cn.Err != null || cn.Tactics.Count != 2 || cn.CorridorX != 30 || cn.BarrierX != 50 || cn.StandDip != 40 || cn.StandBarrier != 60 || cn.StandCorridor.HasValue || cn.Depth != "auto") err = err ?? "course v3 fields";
            if (ParseCourse(P("{\"standoff\":5}")).Err != "bad_args" || ParseCourse(P("{\"corridor_x_nm\":-1}")).Err != "bad_args") err = err ?? "course v3 bad";
            var bn = ParseCourse(P("{\"tactics\":[\"barrier\"],\"barrier_rows\":3,\"barrier_gap_nm\":8}"));   // T-229
            if (bn.Err != null || bn.BarrierRows != 3 || bn.BarrierGap != 8 || ParseCourse(P("{}")).BarrierRows.HasValue || ParseCourse(P("{}")).BarrierGap.HasValue) err = err ?? "course barrier rows / gap";
            if (ParseCourse(P("{\"barrier_rows\":9}")).Err != "bad_args" || ParseCourse(P("{\"barrier_rows\":0}")).Err != "bad_args" || ParseCourse(P("{\"barrier_rows\":2.5}")).Err != "bad_args"
                || ParseCourse(P("{\"barrier_gap_nm\":0}")).Err != "bad_args" || ParseCourse(P("{\"barrier_gap_nm\":-5}")).Err != "bad_args") err = err ?? "course barrier rows / gap bad";
            if (ParseArea(P("{\"bearing\":0,\"dist_nm\":9,\"tactics\":[\"mad\"]}")).Tactics.Count != 1 || ParseArea(P("{\"bearing\":0,\"dist_nm\":9,\"tactics\":[\"x\"]}")).Err != "bad_args") err = err ?? "area tactics";
            var oc = ParseOrder(P("{\"group\":\"УДАР-1\",\"order\":\"strike\",\"ref_uid\":12,\"radius_nm\":30,\"targets\":\"hostile\",\"airborne\":1}"));
            if (oc.Err != null || oc.RefUid != 12 || oc.RadiusNm != 30 || oc.Targets != "hostile" || oc.Airborne != 1) err = err ?? "order ok";
            oc = ParseOrder(P("{\"group\":\"G\",\"order\":\"strike\"}"));
            if (oc.Err != null || oc.RadiusNm.HasValue || oc.Airborne.HasValue || oc.Targets != null) err = err ?? "order defaults";
            if (ParseOrder(P("{\"group\":\"G\",\"order\":\"course\"}")).Err != "bad_args" || ParseOrder(P("{\"order\":\"strike\"}")).Err != "bad_args") err = err ?? "order bad";
            if (ParseOrder(P("{\"group\":\"G\",\"order\":\"strike\",\"radius_nm\":-1}")).Err != "bad_args" || ParseOrder(P("{\"group\":\"G\",\"order\":\"strike\",\"airborne\":9}")).Err != "bad_args"
                || ParseOrder(P("{\"group\":\"G\",\"order\":\"strike\",\"targets\":\"all\"}")).Err != "bad_args") err = err ?? "order bad values";
            return err;
        }
    }

}
