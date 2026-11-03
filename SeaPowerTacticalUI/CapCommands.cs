// T-112 §3.4 CapCommands (pure): parsing of cap_* commands (ADR-0040 §10). Codes are returned without "!":
// missing_group|bad_rect|no_origin|bad_on_station|bad_ready|bad_alt|bad_roe|bad_loadout|bad_param|missing_uids|bad_op
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class CapOrderCmd
    {
        public string Group, Roe; public CapZone Zone; public int OnStation = -1, Ready = -1;
        public double AltFt = -1;   // T-166: patrol altitude of the group, ft; 0 = the game's default; -1 = not given (unchanged)
        // polar form {bearing, dist_nm, size_nm[, w_nm]}: Zone.Rect stays null until the controller resolves the origin (PolarRect);
        // OriginUid 0 and no Origin = the group's carrier (CapWindowModel.OriginShip).
        public bool Polar; public double Bearing, DistNm, SizeNm, WNm; public int OriginUid; public GeoPt? Origin;
        // T-175 / ADR-0075 item 8: loadouts by type in the same order ("auto" = the planner's pick)
        public Dictionary<string, string> Loadouts;
        // T-179 / ADR-0079 section 1 and 9: the order kind. "alert" has no zone; AlertSpec holds the values, AlertGiven the names of the fields the order contained
        // (the controller fills the others from the group's previous alert order or the [Cap] defaults). AcceptAlert (patrol): -1 not given, 0 / 1.
        public string Kind = "patrol"; public CapAlertSpec AlertSpec = new CapAlertSpec(); public HashSet<string> AlertGiven = new HashSet<string>();
        public int AcceptAlert = -1;
        // T-333 (ADR-0129): the zone intercept fields of a patrol order; IcptGiven holds the names (on margin per_target keep targets speed) of the ones the order contained
        public CapInterceptSpec Icpt = new CapInterceptSpec(); public HashSet<string> IcptGiven = new HashSet<string>();
    }

    internal static class CapCommands
    {
        private static readonly string[] Roes = { "tight", "free", "hold" };

        private static string Str(Dictionary<string, object> c, string k) { object v; return c != null && c.TryGetValue(k, out v) ? v as string : null; }
        private static double? Num(Dictionary<string, object> c, string k)
        {
            object v; if (c != null && c.TryGetValue(k, out v) && v is double) return (double)v; return null;
        }

        private static bool IsInt(double d) { return Math.Abs(d - Math.Round(d)) < 1e-9; }

        public static (string op, string group, List<int> uids, string err) ParseGroup(Dictionary<string, object> c)
        {
            string op = Str(c, "op"), group = Str(c, "group");
            var uids = new List<int>();
            if (op != "create" && op != "add" && op != "remove" && op != "disband" && op != "rename") return (op, group, uids, "bad_op");
            object u;
            if (c.TryGetValue("uids", out u))
            {
                var l = u as List<object>;
                if (l == null) return (op, group, uids, "missing_uids");
                foreach (object o in l)
                {
                    if (!(o is double) || !IsInt((double)o)) return (op, group, uids, "missing_uids");
                    uids.Add((int)(double)o);
                }
            }
            if (op != "create" && string.IsNullOrEmpty(group)) return (op, group, uids, "missing_group");
            if (op != "disband" && op != "rename" && uids.Count == 0) return (op, group, uids, "missing_uids");
            return (op, group, uids, null);
        }

        /// <summary>T-326: cap_group rename. err: missing_group | missing_name (no "name" key or not a string). name is trimmed; "" is allowed (= automatic name).</summary>
        public static (string group, string name, string err) ParseRename(Dictionary<string, object> c)
        {
            string group = Str(c, "group");
            if (string.IsNullOrEmpty(group)) return (group, null, "missing_group");
            string name = Str(c, "name");
            if (name == null) return (group, null, "missing_name");
            return (group, name.Trim(), null);
        }

        /// <summary>rect: 4 numbers (lat0,lon0,lat1,lon1) or 8 (four corners, flat or pairs) -> min/max rectangle.</summary>
        private static double[] ReadRect(object o)
        {
            var l = o as List<object>; if (l == null) return null;
            var flat = new List<double>();
            foreach (object x in l)
            {
                if (x is double) flat.Add((double)x);
                else if (x is List<object>)
                {
                    var p = (List<object>)x;
                    if (p.Count != 2 || !(p[0] is double) || !(p[1] is double)) return null;
                    flat.Add((double)p[0]); flat.Add((double)p[1]);
                }
                else return null;
            }
            if (flat.Count != 4 && flat.Count != 8) return null;
            double la0 = double.MaxValue, lo0 = double.MaxValue, la1 = double.MinValue, lo1 = double.MinValue;
            for (int i = 0; i < flat.Count; i += 2)
            {
                if (Math.Abs(flat[i]) > 90 || Math.Abs(flat[i + 1]) > 180) return null;
                la0 = Math.Min(la0, flat[i]); la1 = Math.Max(la1, flat[i]);
                lo0 = Math.Min(lo0, flat[i + 1]); lo1 = Math.Max(lo1, flat[i + 1]);
            }
            if (!(la1 > la0) || !(lo1 > lo0)) return null;
            return new[] { la0, lo0, la1, lo1 };
        }

        /// <summary>Rectangle of a polar order: centre = origin + (bearing, dist); size_nm east-west, w_nm (default size_nm) north-south.</summary>
        public static double[] PolarRect(GeoPt origin, CapOrderCmd c)
        {
            GeoPt ctr = AswMath.Offset(origin, c.Bearing, c.DistNm);
            double w = c.SizeNm / (120.0 * AswMath.LonK(ctr.Lat)), h = (c.WNm > 0 ? c.WNm : c.SizeNm) / 120.0;   // half-sizes in degrees (1 nm = 1/60 deg; east-west x LonK on the globe, T-348)
            return new[] { ctr.Lat - h, ctr.Lon - w, ctr.Lat + h, ctr.Lon + w };
        }

        public static CapOrderCmd ParseOrder(Dictionary<string, object> c, out string err)
        {
            err = null;
            var r = new CapOrderCmd { Group = Str(c, "group"), Zone = new CapZone() };
            if (string.IsNullOrEmpty(r.Group)) { err = "missing_group"; return null; }
            object o;
            string kind = Str(c, "kind");
            if (c.ContainsKey("kind") && kind != "patrol" && kind != "alert") { err = "bad_kind"; return null; }
            bool hasZone = c.ContainsKey("rect") || c.ContainsKey("bearing");
            int compatReady = 0;
            if (c.ContainsKey("alert"))
            {
                // ADR-0079 section 9: the old field. Without a zone it is the alert group (compatibility with dev scripts), with a zone it moved.
                double? a = Num(c, "alert");
                if (!a.HasValue || !IsInt(a.Value) || a.Value < 0 || a.Value > 4) { err = "bad_alert"; return null; }
                if (a.Value > 0)
                {
                    if (hasZone || kind == "patrol") { err = "alert_moved"; return null; }
                    compatReady = (int)a.Value;
                    kind = "alert";
                }
            }
            if (kind == "alert") { r.Kind = "alert"; if (!ReadAlert(c, r, compatReady, out err)) return null; }
            else
            {
                object ac;
                if (c.TryGetValue("accept_alert", out ac)) { if (!(ac is bool)) { err = "bad_param"; return null; } r.AcceptAlert = (bool)ac ? 1 : 0; }
                if (!ReadIcpt(c, r, out err)) return null;
            }
            if (c.TryGetValue("loadouts", out o))
            {
                var d = o as Dictionary<string, object>;
                if (d == null) { err = "bad_loadout"; return null; }
                r.Loadouts = new Dictionary<string, string>();
                foreach (var kv in d)
                {
                    var name = kv.Value as string;
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(name)) { err = "bad_loadout"; return null; }
                    r.Loadouts[kv.Key] = name;
                }
            }
            if (r.Kind == "alert")
            {
                if (c.ContainsKey("roe"))
                {
                    r.Roe = Str(c, "roe");
                    if (r.Roe == null || Array.IndexOf(Roes, r.Roe) < 0) { err = "bad_roe"; return null; }
                    r.AlertSpec.Roe = r.Roe; r.AlertGiven.Add("roe");
                }
                return r;   // the alert group has no zone: on_station / ready (of a zone) / alt_ft are not read
            }
            if (c.TryGetValue("rect", out o))
            {
                r.Zone.Rect = ReadRect(o);
                if (r.Zone.Rect == null) { err = "bad_rect"; return null; }
            }
            else
            {
                double? b = Num(c, "bearing"), d = Num(c, "dist_nm"), s = Num(c, "size_nm");
                if (!b.HasValue || !d.HasValue || !s.HasValue || d.Value <= 0 || d.Value > 300 || s.Value <= 0 || s.Value > 200) { err = "bad_rect"; return null; }
                double? w = Num(c, "w_nm");
                if (w.HasValue && (w.Value <= 0 || w.Value > 200)) { err = "bad_rect"; return null; }
                r.Polar = true; r.Bearing = AswMath.Norm360(b.Value); r.DistNm = d.Value; r.SizeNm = s.Value; r.WNm = w ?? 0;
                double? ou = Num(c, "origin_uid"), la = Num(c, "origin_lat"), lo = Num(c, "origin_lon");
                if (ou.HasValue) r.OriginUid = (int)ou.Value;
                else if (la.HasValue && lo.HasValue && Math.Abs(la.Value) <= 90 && Math.Abs(lo.Value) <= 180) r.Origin = new GeoPt(la.Value, lo.Value);
                // neither: OriginUid stays 0 = the group's carrier, resolved by the controller (as the CAP window does; live finding 37)
                if (r.Origin.HasValue) { r.Zone.Rect = PolarRect(r.Origin.Value, r); }
            }
            double? au = Num(c, "anchor_uid");
            if (au.HasValue && au.Value != 0) { r.Zone.Anchor = "unit"; r.Zone.AnchorUid = (int)au.Value; }
            if (c.ContainsKey("on_station"))
            {
                double? n = Num(c, "on_station");
                if (!n.HasValue || !IsInt(n.Value) || n.Value < 1 || n.Value > 50) { err = "bad_on_station"; return null; }
                r.OnStation = (int)n.Value;
            }
            if (c.ContainsKey("ready"))
            {
                double? n = Num(c, "ready");
                if (!n.HasValue || !IsInt(n.Value) || n.Value < 0 || n.Value > 50) { err = "bad_ready"; return null; }
                r.Ready = (int)n.Value;
            }
            if (c.ContainsKey("alt_ft"))
            {
                double? a = Num(c, "alt_ft");
                if (!a.HasValue || a.Value < 0 || a.Value > 60000) { err = "bad_alt"; return null; }
                r.AltFt = a.Value;
            }
            if (r.Polar)
            {
                r.Zone.HasPolar = true; r.Zone.PolarBearing = r.Bearing; r.Zone.PolarDistNm = r.DistNm; r.Zone.PolarSizeNm = r.SizeNm; r.Zone.PolarWNm = r.WNm;
            }
            if (c.ContainsKey("roe"))
            {
                r.Roe = Str(c, "roe");
                if (r.Roe == null || Array.IndexOf(Roes, r.Roe) < 0) { err = "bad_roe"; return null; }
            }
            return r;
        }

        /// <summary>T-333: intercept fields of a patrol order. Errors: bad_param (intercept not a bool) bad_margin bad_per_target bad_keep bad_targets bad_speed.</summary>
        private static bool ReadIcpt(Dictionary<string, object> c, CapOrderCmd r, out string err)
        {
            err = null;
            var a = r.Icpt; var g = r.IcptGiven; object o; double? n;
            if (c.TryGetValue("intercept", out o)) { if (!(o is bool)) { err = "bad_param"; return false; } a.On = (bool)o; g.Add("on"); }
            if (c.ContainsKey("icpt_margin_nm"))
            {
                n = Num(c, "icpt_margin_nm");
                if (!n.HasValue || n.Value < 0 || n.Value > 50) { err = "bad_margin"; return false; }
                a.MarginNm = n.Value; g.Add("margin");
            }
            if (c.ContainsKey("per_target"))
            {
                n = Num(c, "per_target");
                if (!n.HasValue || !IsInt(n.Value) || n.Value < 1 || n.Value > 4) { err = "bad_per_target"; return false; }
                a.PerTarget = (int)n.Value; g.Add("per_target");
            }
            if (c.ContainsKey("keep"))
            {
                n = Num(c, "keep");
                if (!n.HasValue || !IsInt(n.Value) || n.Value < 0 || n.Value > 50) { err = "bad_keep"; return false; }
                a.Keep = (int)n.Value; g.Add("keep");
            }
            if (c.ContainsKey("icpt_targets"))
            {
                object t = c["icpt_targets"]; string csv;
                var l = t as List<object>;
                if (l != null)
                {
                    var parts = new List<string>();
                    foreach (object x in l) { var sx = x as string; if (sx == null) { err = "bad_targets"; return false; } parts.Add(sx); }
                    csv = string.Join(",", parts);
                }
                else csv = t as string;
                bool ok; var tg = CapAlertMath.ParseTargets(csv, out ok);
                if (!ok) { err = "bad_targets"; return false; }
                a.Targets = CapAlertMath.TargetsCsv(tg); g.Add("targets");
            }
            if (c.ContainsKey("icpt_speed"))
            {
                string v = Str(c, "icpt_speed");
                if (v != "max" && v != "cruise") { err = "bad_speed"; return false; }
                a.Speed = v; g.Add("speed");
            }
            return true;
        }

        /// <summary>Fields of a kind=alert order (ADR-0079 section 9). Errors: bad_ready bad_per_raid bad_level bad_radius bad_targets bad_pursuit bad_speed bad_after bad_param.</summary>
        private static bool ReadAlert(Dictionary<string, object> c, CapOrderCmd r, int compatReady, out string err)
        {
            err = null;
            var a = r.AlertSpec; var g = r.AlertGiven;
            if (compatReady > 0) { a.Ready = compatReady; g.Add("ready"); }
            double? n;
            if (c.ContainsKey("ready"))
            {
                n = Num(c, "ready");
                if (!n.HasValue || !IsInt(n.Value) || n.Value < 1 || n.Value > 4) { err = "bad_ready"; return false; }
                a.Ready = (int)n.Value; g.Add("ready");
            }
            if (c.ContainsKey("per_raid"))
            {
                n = Num(c, "per_raid");
                if (!n.HasValue || !IsInt(n.Value) || n.Value < 0 || n.Value > 4) { err = "bad_per_raid"; return false; }
                a.PerRaid = (int)n.Value; g.Add("per_raid");
            }
            if (c.ContainsKey("level"))
            {
                string v = Str(c, "level");
                if (v != "deck" && v != "hangar") { err = "bad_level"; return false; }
                a.Level = v; g.Add("level");
            }
            if (c.ContainsKey("radius_nm"))
            {
                n = Num(c, "radius_nm");
                if (!n.HasValue || n.Value < 10 || n.Value > 300) { err = "bad_radius"; return false; }
                a.RadiusNm = n.Value; g.Add("radius_nm");
            }
            if (c.ContainsKey("targets"))
            {
                object t = c["targets"]; string csv = null;
                var l = t as List<object>;
                if (l != null)
                {
                    var parts = new List<string>();
                    foreach (object x in l) { var sx = x as string; if (sx == null) { err = "bad_targets"; return false; } parts.Add(sx); }
                    csv = string.Join(",", parts);
                }
                else csv = t as string;
                bool ok; var tg = CapAlertMath.ParseTargets(csv, out ok);
                if (!ok) { err = "bad_targets"; return false; }
                a.Targets = tg; g.Add("targets");
            }
            if (c.ContainsKey("pursuit_nm"))
            {
                n = Num(c, "pursuit_nm");
                if (!n.HasValue || n.Value < 10 || n.Value > 300) { err = "bad_pursuit"; return false; }
                a.PursuitNm = n.Value; g.Add("pursuit_nm");
            }
            if (c.ContainsKey("speed"))
            {
                string v = Str(c, "speed");
                if (v != "max" && v != "cruise") { err = "bad_speed"; return false; }
                a.Speed = v; g.Add("speed");
            }
            if (c.ContainsKey("after"))
            {
                string v = Str(c, "after");
                if (v != "alert" && v != "patrol") { err = "bad_after"; return false; }
                a.After = v; g.Add("after");
            }
            object b;
            if (c.TryGetValue("fill", out b)) { if (!(b is bool)) { err = "bad_param"; return false; } a.Fill = (bool)b; g.Add("fill"); }
            if (c.ContainsKey("ref_uid") || c.ContainsKey("alert_ref_uid"))
            {
                n = Num(c, c.ContainsKey("ref_uid") ? "ref_uid" : "alert_ref_uid");
                if (!n.HasValue || !IsInt(n.Value) || n.Value < 0) { err = "bad_param"; return false; }
                a.RefUid = (int)n.Value; g.Add("ref_uid");
            }
            return true;
        }

        /// <summary>cap_scramble {group} -> group, or null + missing_group.</summary>
        public static string ParseScramble(Dictionary<string, object> c, out string err)
        {
            err = null;
            string g = Str(c, "group");
            if (string.IsNullOrEmpty(g)) { err = "missing_group"; return null; }
            return g;
        }

        public static bool ParseLoadout(Dictionary<string, object> c, out string group, out string type, out string loadout, out string err)
        {
            group = Str(c, "group"); type = Str(c, "type"); loadout = Str(c, "loadout"); err = null;
            if (string.IsNullOrEmpty(group)) { err = "missing_group"; return false; }
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(loadout)) { err = "bad_loadout"; return false; }
            return true;
        }

        public static bool ParseParams(Dictionary<string, object> c, CapParams into, out string err)
        {
            err = null;
            double? mp = null, fm = null, ff = null, rm = null, alt = null; bool? nl = null, dr = null, ao = null, rh = null;
            if (c.ContainsKey("missile_pct")) { mp = Num(c, "missile_pct"); if (!mp.HasValue || mp.Value < 0 || mp.Value > 1) { err = "bad_param"; return false; } }
            if (c.ContainsKey("fuel_margin_s")) { fm = Num(c, "fuel_margin_s"); if (!fm.HasValue || fm.Value < 0 || fm.Value > 86400) { err = "bad_param"; return false; } }
            if (c.ContainsKey("ring_margin_nm")) { rm = Num(c, "ring_margin_nm"); if (!rm.HasValue || rm.Value < 0 || rm.Value > 500) { err = "bad_param"; return false; } }   // T-330
            if (c.ContainsKey("fuel_floor_pct")) { ff = Num(c, "fuel_floor_pct"); if (!ff.HasValue || ff.Value < 0 || ff.Value > 0.5) { err = "bad_param"; return false; } }
            if (c.ContainsKey("alt_ft")) { alt = Num(c, "alt_ft"); if (!alt.HasValue || alt.Value < 0 || alt.Value > 60000) { err = "bad_param"; return false; } }
            object b;
            if (c.TryGetValue("need_long_range", out b)) { if (!(b is bool)) { err = "bad_param"; return false; } nl = (bool)b; }
            if (c.TryGetValue("dash_rtb", out b)) { if (!(b is bool)) { err = "bad_param"; return false; } dr = (bool)b; }
            if (c.TryGetValue("air_only", out b)) { if (!(b is bool)) { err = "bad_param"; return false; } ao = (bool)b; }
            if (c.TryGetValue("rtb_hold", out b)) { if (!(b is bool)) { err = "bad_param"; return false; } rh = (bool)b; }
            if (!mp.HasValue && !fm.HasValue && !rm.HasValue && !ff.HasValue && !nl.HasValue && !dr.HasValue && !alt.HasValue && !ao.HasValue && !rh.HasValue) { err = "bad_param"; return false; }
            if (mp.HasValue) into.MissilePct = mp.Value;
            if (fm.HasValue) into.FuelMarginS = fm.Value;
            if (rm.HasValue) into.RingMarginNm = rm.Value;
            if (ff.HasValue) into.FuelFloorPct = ff.Value;
            if (nl.HasValue) into.NeedLongRange = nl.Value;
            if (dr.HasValue) into.DashOnMissileRtb = dr.Value;
            if (alt.HasValue) into.PatrolAltFt = alt.Value;
            if (ao.HasValue) into.AirOnly = ao.Value;
            if (rh.HasValue) into.RtbRoeHold = rh.Value;
            return true;
        }

        /// <summary>group name or "all"; null + err = missing_group.</summary>
        public static string ParseStop(Dictionary<string, object> c, out string err)
        {
            err = null;
            object a;
            if (c.TryGetValue("all", out a) && a is bool && (bool)a) return "all";
            string g = Str(c, "group");
            if (string.IsNullOrEmpty(g)) { err = "missing_group"; return null; }
            return g;
        }

        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        /// <summary>null = ok, otherwise error text.</summary>
        public static string SelfTest()
        {
            string err;
            var g = ParseGroup(P("{\"op\":\"create\",\"group\":\"ПВО-1\",\"uids\":[1,2]}"));
            if (g.err != null || g.uids.Count != 2 || g.group != "ПВО-1") return "group create";
            if (ParseGroup(P("{\"op\":\"create\",\"uids\":[3]}")).err != null) return "group create unnamed";
            if (ParseGroup(P("{\"op\":\"add\",\"group\":\"G\",\"uids\":[3]}")).err != null) return "group add";
            if (ParseGroup(P("{\"op\":\"remove\",\"group\":\"G\",\"uids\":[3]}")).err != null) return "group remove";
            if (ParseGroup(P("{\"op\":\"disband\",\"group\":\"G\"}")).err != null) return "group disband";
            if (ParseGroup(P("{\"op\":\"zap\"}")).err != "bad_op") return "group bad_op";
            if (ParseGroup(P("{\"op\":\"add\",\"uids\":[3]}")).err != "missing_group") return "group missing_group";
            if (ParseGroup(P("{\"op\":\"add\",\"group\":\"G\"}")).err != "missing_uids") return "group missing_uids";
            if (ParseGroup(P("{\"op\":\"add\",\"group\":\"G\",\"uids\":[\"x\"]}")).err != "missing_uids") return "group bad uid";
            if (ParseGroup(P("{\"op\":\"disband\"}")).err != "missing_group") return "disband needs group";

            if (ParseGroup(P("{\"op\":\"rename\",\"group\":\"G\"}")).err != null) return "group rename";
            if (ParseGroup(P("{\"op\":\"rename\"}")).err != "missing_group") return "rename needs group";
            var rn = ParseRename(P("{\"group\":\"G\"}"));
            if (rn.err != "missing_name") return "rename without name";
            rn = ParseRename(P("{\"group\":\"G\",\"name\":\" Альфа \"}"));
            if (rn.err != null || rn.name != "Альфа" || rn.group != "G") return "rename trims";
            rn = ParseRename(P("{\"group\":\"G\",\"name\":\"\"}"));
            if (rn.err != null || rn.name != "") return "rename empty";
            if (ParseRename(P("{\"group\":\"G\",\"name\":5}")).err != "missing_name") return "rename name not a string";
            if (ParseRename(P("{\"name\":\"A\"}")).err != "missing_group") return "rename missing_group";

            var o4 = ParseOrder(P("{\"group\":\"G\",\"rect\":[35.3,20.7,35,20],\"on_station\":2,\"ready\":1,\"roe\":\"tight\"}"), out err);
            if (err != null || o4.Zone.Rect[0] != 35 || o4.Zone.Rect[3] != 20.7 || o4.OnStation != 2 || o4.Ready != 1 || o4.Roe != "tight") return "order rect4 " + err;
            var o8 = ParseOrder(P("{\"group\":\"G\",\"rect\":[[35,20],[35,20.2],[35.1,20.2],[35.1,20]]}"), out err);
            if (err != null || o8.Zone.Rect[2] != 35.1 || o8.Zone.Rect[3] != 20.2 || o8.OnStation != -1) return "order rect8 pairs " + err;
            var o8f = ParseOrder(P("{\"group\":\"G\",\"rect\":[35,20,35,20.2,35.1,20.2,35.1,20]}"), out err);
            if (err != null || o8f.Zone.Rect[1] != 20) return "order rect8 flat " + err;
            ParseOrder(P("{\"group\":\"G\",\"rect\":[95,20,35,21]}"), out err); if (err != "bad_rect") return "order bad lat";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,2,3]}"), out err); if (err != "bad_rect") return "order short rect";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[35,20,35,21]}"), out err); if (err != "bad_rect") return "order flat rect";
            ParseOrder(P("{\"rect\":[35,20,36,21]}"), out err); if (err != "missing_group") return "order missing_group";
            var pl = ParseOrder(P("{\"group\":\"G\",\"bearing\":370,\"dist_nm\":60,\"size_nm\":40,\"w_nm\":20,\"origin_uid\":9,\"anchor_uid\":9}"), out err);
            if (err != null || !pl.Polar || pl.Bearing != 10 || pl.OriginUid != 9 || pl.Zone.Anchor != "unit" || pl.Zone.AnchorUid != 9) return "order polar " + err;
            var pr = PolarRect(new GeoPt(35, 20), pl);
            if (Math.Abs((pr[2] - pr[0]) * 60 - 20) > 1e-6 || Math.Abs((pr[3] - pr[1]) * 60 - 40) > 1e-6) return "polar size";
            var pg = ParseOrder(P("{\"group\":\"G\",\"bearing\":0,\"dist_nm\":30,\"size_nm\":10,\"origin_lat\":35,\"origin_lon\":20}"), out err);
            if (err != null || pg.Zone.Rect == null || Math.Abs((pg.Zone.Rect[0] + pg.Zone.Rect[2]) / 2 - 35.5) > 1e-9) return "order polar latlon " + err;
            var pd = ParseOrder(P("{\"group\":\"G\",\"bearing\":0,\"dist_nm\":30,\"size_nm\":10}"), out err);
            if (err != null || pd == null || pd.OriginUid != 0 || pd.Origin != null || pd.Zone.Rect != null) return "order polar default origin " + err;
            ParseOrder(P("{\"group\":\"G\",\"bearing\":0,\"size_nm\":10,\"origin_uid\":1}"), out err); if (err != "bad_rect") return "order polar no dist";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[35,20,36,21],\"on_station\":0}"), out err); if (err != "bad_on_station") return "on_station 0";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[35,20,36,21],\"on_station\":1.5}"), out err); if (err != "bad_on_station") return "on_station frac";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[35,20,36,21],\"ready\":-1}"), out err); if (err != "bad_ready") return "ready -1";
            var r0 = ParseOrder(P("{\"group\":\"G\",\"rect\":[35,20,36,21],\"ready\":0}"), out err); if (err != null || r0.Ready != 0) return "ready 0 ok";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[35,20,36,21],\"roe\":\"fire\"}"), out err); if (err != "bad_roe") return "bad_roe";

            string gr, ty, lo;
            if (!ParseLoadout(P("{\"group\":\"G\",\"type\":\"F-14A\",\"loadout\":\"auto\"}"), out gr, out ty, out lo, out err) || lo != "auto" || ty != "F-14A") return "loadout auto";
            if (ParseLoadout(P("{\"group\":\"G\",\"type\":\"F-14A\"}"), out gr, out ty, out lo, out err) || err != "bad_loadout") return "loadout missing";
            if (ParseLoadout(P("{\"type\":\"F-14A\",\"loadout\":\"x\"}"), out gr, out ty, out lo, out err) || err != "missing_group") return "loadout group";

            var cp = new CapParams();
            if (!ParseParams(P("{\"group\":\"G\",\"missile_pct\":0.5,\"fuel_margin_s\":120,\"fuel_floor_pct\":0.2,\"ring_margin_nm\":7,\"need_long_range\":true}"), cp, out err)
                || cp.RingMarginNm != 7 || cp.MissilePct != 0.5 || cp.FuelMarginS != 120 || cp.FuelFloorPct != 0.2 || !cp.NeedLongRange) return "params ok " + err;
            var cq = new CapParams();
            if (ParseParams(P("{\"missile_pct\":1.5}"), cq, out err) || err != "bad_param") return "missile_pct range";
            if (ParseParams(P("{\"fuel_margin_s\":-1}"), cq, out err) || err != "bad_param") return "fuel_margin range";
            if (ParseParams(P("{\"ring_margin_nm\":-1}"), cq, out err) || err != "bad_param") return "ring_margin low";
            if (ParseParams(P("{\"ring_margin_nm\":600}"), cq, out err) || err != "bad_param") return "ring_margin high";
            if (!ParseParams(P("{\"ring_margin_nm\":3}"), cq, out err) || cq.RingMarginNm != 3) return "ring_margin only " + err;
            if (ParseParams(P("{\"fuel_floor_pct\":0.6}"), cq, out err) || err != "bad_param") return "fuel_floor range";
            if (ParseParams(P("{\"missile_pct\":0.1,\"fuel_floor_pct\":0.9}"), cq, out err) || cq.MissilePct != 0.20) return "params atomic";
            if (ParseParams(P("{\"need_long_range\":\"yes\"}"), cq, out err) || err != "bad_param") return "need_long_range type";
            if (ParseParams(P("{\"group\":\"G\"}"), cq, out err) || err != "bad_param") return "params empty";
            if (!ParseParams(P("{\"group\":\"G\",\"alt_ft\":12000}"), cq, out err) || cq.PatrolAltFt != 12000) return "params alt " + err;
            if (ParseParams(P("{\"alt_ft\":-5}"), cq, out err) || err != "bad_param" || cq.PatrolAltFt != 12000) return "params alt range";
            var oa = ParseOrder(P("{\"group\":\"G\",\"bearing\":300,\"dist_nm\":40,\"size_nm\":30,\"origin_lat\":35,\"origin_lon\":20,\"alt_ft\":8000}"), out err);
            if (oa == null || oa.AltFt != 8000 || !oa.Zone.HasPolar || oa.Zone.PolarBearing != 300 || oa.Zone.PolarSizeNm != 30) return "order alt + polar " + err;
            if (ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"alt_ft\":99999}"), out err) != null || err != "bad_alt") return "order alt range";
            var on = ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2]}"), out err);
            if (on == null || on.AltFt != -1 || on.Zone.HasPolar) return "order without alt";

            // T-175: loadouts, air_only / rtb_hold. T-179: the alert moved to kind=alert
            var ol = ParseOrder(P("{\"group\":\"G\",\"rect\":[35,20,36,21],\"loadouts\":{\"F-14A\":\"AirToAirIntercept\",\"F-15C\":\"auto\"}}"), out err);
            if (ol == null || ol.Loadouts.Count != 2 || ol.Loadouts["F-14A"] != "AirToAirIntercept" || ol.Loadouts["F-15C"] != "auto" || ol.Kind != "patrol") return "order loadouts " + err;
            ParseOrder(P("{\"group\":\"G\",\"rect\":[35,20,36,21],\"alert\":2,\"alert_ref_uid\":7}"), out err); if (err != "alert_moved") return "alert with a zone: alert_moved " + err;
            var oz = ParseOrder(P("{\"group\":\"G\",\"alert\":2,\"alert_ref_uid\":7}"), out err);
            if (oz == null || oz.Kind != "alert" || oz.AlertSpec.Ready != 2 || oz.AlertSpec.RefUid != 7 || oz.Zone.Rect != null || !oz.AlertGiven.Contains("ready") || oz.AlertGiven.Contains("level")) return "old alert without a zone = alert group " + err;
            ParseOrder(P("{\"group\":\"G\"}"), out err); if (err != "bad_rect") return "order without zone and alert";
            ParseOrder(P("{\"group\":\"G\",\"alert\":0}"), out err); if (err != "bad_rect") return "alert 0 without zone";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"alert\":5}"), out err); if (err != "bad_alert") return "alert 5";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"alert\":1.5}"), out err); if (err != "bad_alert") return "alert 1.5";
            // T-333 (ADR-0129): intercept fields
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"icpt_margin_nm\":-1}"), out err); if (err != "bad_margin") return "icpt margin low";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"icpt_margin_nm\":51}"), out err); if (err != "bad_margin") return "icpt margin high";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"per_target\":0}"), out err); if (err != "bad_per_target") return "per_target 0";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"per_target\":5}"), out err); if (err != "bad_per_target") return "per_target 5";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"per_target\":1.5}"), out err); if (err != "bad_per_target") return "per_target 1.5";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"keep\":51}"), out err); if (err != "bad_keep") return "keep 51";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"icpt_targets\":\"\"}"), out err); if (err != "bad_targets") return "icpt_targets empty";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"icpt_targets\":[\"tank\"]}"), out err); if (err != "bad_targets") return "icpt_targets garbage";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"icpt_speed\":\"fast\"}"), out err); if (err != "bad_speed") return "icpt_speed";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"intercept\":1}"), out err); if (err != "bad_param") return "intercept not bool";
            var ic = ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"intercept\":false,\"icpt_margin_nm\":20,\"per_target\":1,\"keep\":2,\"icpt_targets\":[\"helo\",\"air\"],\"icpt_speed\":\"cruise\"}"), out err);
            if (ic == null || ic.Icpt.On || ic.Icpt.MarginNm != 20 || ic.Icpt.PerTarget != 1 || ic.Icpt.Keep != 2 || ic.Icpt.Targets != "air,helo" || ic.Icpt.Speed != "cruise" || ic.IcptGiven.Count != 6) return "icpt fields " + err;
            var icn = ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2]}"), out err);
            if (icn == null || icn.IcptGiven.Count != 0) return "icpt not given";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"loadouts\":[1]}"), out err); if (err != "bad_loadout") return "loadouts not an object";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"loadouts\":{\"F-14A\":\"\"}}"), out err); if (err != "bad_loadout") return "loadouts empty name";
            var oa0 = ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"alert\":0}"), out err);
            if (oa0 == null || oa0.Kind != "patrol") return "alert 0 with a zone: ignored " + err;

            // T-179: kind=alert
            var ka = ParseOrder(P("{\"group\":\"G\",\"kind\":\"alert\",\"ref_uid\":9,\"ready\":3,\"per_raid\":0,\"level\":\"hangar\",\"radius_nm\":100,\"targets\":[\"air\",\"unknown\"],\"pursuit_nm\":80,\"speed\":\"cruise\",\"roe\":\"hold\",\"after\":\"alert\",\"fill\":true,\"loadouts\":{\"F-14A\":\"auto\"}}"), out err);
            if (ka == null || ka.Kind != "alert" || ka.Zone.Rect != null) return "kind alert " + err;
            var kas = ka.AlertSpec;
            if (kas.RefUid != 9 || kas.Ready != 3 || kas.PerRaid != 0 || kas.Level != "hangar" || kas.RadiusNm != 100 || kas.Targets != (AlertTargets.Air | AlertTargets.Unknown) || kas.PursuitNm != 80
                || kas.Speed != "cruise" || kas.Roe != "hold" || ka.Roe != "hold" || kas.After != "alert" || !kas.Fill || ka.AlertGiven.Count != 11 || ka.Loadouts == null) return "kind alert fields " + ka.AlertGiven.Count;
            var kmin = ParseOrder(P("{\"group\":\"G\",\"kind\":\"alert\"}"), out err);
            if (kmin == null || kmin.AlertGiven.Count != 0 || kmin.Kind != "alert") return "kind alert alone " + err;
            var kcsv = ParseOrder(P("{\"group\":\"G\",\"kind\":\"alert\",\"targets\":\"helo,air\"}"), out err);
            if (kcsv == null || kcsv.AlertSpec.Targets != (AlertTargets.Air | AlertTargets.Helo)) return "targets csv " + err;
            var kp = ParseOrder(P("{\"group\":\"G\",\"kind\":\"patrol\",\"rect\":[1,1,2,2],\"accept_alert\":false}"), out err);
            if (kp == null || kp.Kind != "patrol" || kp.AcceptAlert != 0) return "patrol accept_alert " + err;
            var kp1 = ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2]}"), out err);
            if (kp1 == null || kp1.AcceptAlert != -1) return "accept_alert not given";
            ParseOrder(P("{\"group\":\"G\",\"rect\":[1,1,2,2],\"accept_alert\":1}"), out err); if (err != "bad_param") return "accept_alert type";
            string[][] bad =
            {
                new[] { "bad_kind", "\"kind\":\"zap\"" }, new[] { "bad_ready", "\"kind\":\"alert\",\"ready\":0" }, new[] { "bad_ready", "\"kind\":\"alert\",\"ready\":5" },
                new[] { "bad_ready", "\"kind\":\"alert\",\"ready\":1.5" }, new[] { "bad_per_raid", "\"kind\":\"alert\",\"per_raid\":5" }, new[] { "bad_per_raid", "\"kind\":\"alert\",\"per_raid\":-1" },
                new[] { "bad_level", "\"kind\":\"alert\",\"level\":\"sky\"" }, new[] { "bad_radius", "\"kind\":\"alert\",\"radius_nm\":5" }, new[] { "bad_radius", "\"kind\":\"alert\",\"radius_nm\":301" },
                new[] { "bad_targets", "\"kind\":\"alert\",\"targets\":[]" }, new[] { "bad_targets", "\"kind\":\"alert\",\"targets\":\"\"" }, new[] { "bad_targets", "\"kind\":\"alert\",\"targets\":[\"tank\"]" },
                new[] { "bad_targets", "\"kind\":\"alert\",\"targets\":7" }, new[] { "bad_pursuit", "\"kind\":\"alert\",\"pursuit_nm\":9" }, new[] { "bad_pursuit", "\"kind\":\"alert\",\"pursuit_nm\":400" },
                new[] { "bad_speed", "\"kind\":\"alert\",\"speed\":\"warp\"" }, new[] { "bad_after", "\"kind\":\"alert\",\"after\":\"home\"" }, new[] { "bad_roe", "\"kind\":\"alert\",\"roe\":\"fire\"" },
                new[] { "bad_param", "\"kind\":\"alert\",\"fill\":1" }, new[] { "alert_moved", "\"kind\":\"patrol\",\"alert\":2" },
            };
            foreach (var bc in bad)
            {
                ParseOrder(P("{\"group\":\"G\"," + bc[1] + "}"), out err);
                if (err != bc[0]) return "kind alert error " + bc[0] + " got " + err + " for " + bc[1];
            }
            var cr = new CapParams();
            if (!ParseParams(P("{\"air_only\":false,\"rtb_hold\":false}"), cr, out err) || cr.AirOnly || cr.RtbRoeHold) return "params air_only / rtb_hold " + err;
            if (ParseParams(P("{\"air_only\":1}"), cr, out err) || err != "bad_param") return "air_only type";

            if (ParseScramble(P("{\"group\":\"ПВО-2\"}"), out err) != "ПВО-2" || err != null) return "scramble group";
            if (ParseScramble(P("{}"), out err) != null || err != "missing_group") return "scramble missing_group";
            if (ParseStop(P("{\"group\":\"all\"}"), out err) != "all" || err != null) return "stop group all";
            if (ParseStop(P("{\"all\":true}"), out err) != "all") return "stop all flag";
            if (ParseStop(P("{\"group\":\"ПВО-2\"}"), out err) != "ПВО-2") return "stop group";
            if (ParseStop(P("{}"), out err) != null || err != "missing_group") return "stop empty";
            return null;
        }
    }
}
