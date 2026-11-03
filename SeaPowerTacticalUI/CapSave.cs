// T-112 §3.4 CapSave (pure): JSON of cap/<sha1>.json (ADR-0040 §12). Key/fresh/match rules are AswSave's (not reused here: no game dependency).
using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed class CapMemberDto
    {
        public int Uid; public string Name, Type; public int Slot = -1, AamStart, LongStart;
        public int Deck;   // T-186 (ADR-0082 s.6): uid of the ship of a member that sits in a hangar slot (format 3), else 0
        public int TaskDeck;       // T-218: ship of a deck member on a task / cool-down row (not a hangar slot, not airborne); 0 = none / old file
        public string Where;       // T-218: deck_ready | deck_prep | deck_cool | hangar at save time; null = old file
        public string Loadout;     // T-218: loadout of the row at save time; null = unknown
        public CapAlertBindDto Bind;   // T-219 (ADR-0090): alert binding of the member (state, raid, AirGuard key, last target point); null = none / old file
        public string LentFrom, LentTo; public bool AutoFill;   // T-179: an alert bird lent to a patrol group (LentFrom is written on the patrol side, LentTo on the alert side); taken from the deck by "Fill"
    }

    internal sealed class CapGroupDto
    {
        public string Group; public int Seq, OnStation, Ready; public string Roe; public double[] Rect;
        public string Anchor; public int AnchorUid; public double OffLat, OffLon;
        // T-179 / ADR-0079 section 11 (format 2): kind patrol | alert, the settings of an alert group, "accept alert aircraft" of a patrol group;
        // Migrated: null | "alert_group" | "dropped" (only when a format 1 file is read)
        public string Kind = "patrol"; public CapAlertSpec AlertSpec; public bool AcceptAlert = true; public string Migrated;
        public Dictionary<string, string> Loadouts; public CapParams P; public List<CapMemberDto> Members; public List<string> Keys;
        public List<RaidMemo> Memos;   // T-219 (ADR-0090): alert kind - place memory of the served raids; null = none / old file
        public CapInterceptSpec Icpt;   // T-333 (ADR-0129): the zone intercept settings of a patrol group ("intercept" block); null = none / old file (the [Cap] defaults on load)
        public bool? AutoName;   // T-326: "auto_name" is written only when set; null = old file (derived from the name on load)
        public List<int> Served; public List<string> ServedRaids;   // T-211 (ADR-0088): alert kind - track ids and AirGuard raid keys already served; null = none
    }

    internal sealed class CapMemDto { public int Uid; public string Roe; public List<double[]> Route; public int Throttle = -1; public bool Dash;
        public AirRadarDto Radar; public bool RadarOnly; }   // T-241 (ADR-0099): radar memory of the aircraft (JSON "radar"); RadarOnly: the entry carries no ROE / dash memory   // Throttle/Dash: ADR-0040 D2 (previous speed step; set while a fast return is in progress)

    internal sealed class CapSaveData
    {
        public int Format = CapSave.CurrentFormat; public double MissionTimeS;
        public SavStamp Sav;   // T-198 (ADR-0086): stamp of the game save this file was written for; null = old file
        public List<CapGroupDto> Groups = new List<CapGroupDto>();
        public List<CapMemDto> Memory = new List<CapMemDto>();
    }

    internal static class CapSave
    {
        public const int CurrentFormat = 3;

        // ---- writer -------------------------------------------------------------------------------------------------------------

        public static string ToJson(CapSaveData d)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"format\":").Append(d.Format).Append(",\"mission_time_s\":").Append(Json.Num(d.MissionTimeS));
            if (d.Sav != null) sb.Append(",\"sav\":").Append(d.Sav.ToJson());
            sb.Append(",\"groups\":[");
            for (int i = 0; i < d.Groups.Count; i++) { if (i > 0) sb.Append(','); Group(sb, d.Groups[i], d.MissionTimeS); }
            sb.Append("],\"memory\":[");
            for (int i = 0; i < d.Memory.Count; i++) { if (i > 0) sb.Append(','); Mem(sb, d.Memory[i]); }
            return sb.Append("]}").ToString();
        }

        private static void Arr(StringBuilder sb, IList<double> a)
        {
            sb.Append('[');
            if (a != null) for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Json.Num(a[i])); }
            sb.Append(']');
        }

        private static void Group(StringBuilder sb, CapGroupDto g, double missionTimeS)
        {
            sb.Append("{\"group\":").Append(Json.Str(g.Group)).Append(",\"seq\":").Append(g.Seq);
            sb.Append(",\"on_station\":").Append(g.OnStation).Append(",\"ready\":").Append(g.Ready);
            sb.Append(",\"roe\":").Append(Json.Str(g.Roe));
            sb.Append(",\"kind\":").Append(Json.Str(g.Kind ?? "patrol"));
            if (g.Kind == "alert" && g.AlertSpec != null) AlertBlock(sb, g.AlertSpec); else sb.Append(",\"accept_alert\":").Append(Json.Bool(g.AcceptAlert));
            if (g.Kind != "alert" && g.Icpt != null) IcptBlock(sb, g.Icpt);   // T-333
            sb.Append(",\"rect\":"); Arr(sb, g.Rect);
            sb.Append(",\"anchor\":").Append(Json.Str(g.Anchor ?? "geo")).Append(",\"anchor_uid\":").Append(g.AnchorUid);
            sb.Append(",\"off_lat\":").Append(Json.Num(g.OffLat)).Append(",\"off_lon\":").Append(Json.Num(g.OffLon));
            sb.Append(",\"loadouts\":{");
            if (g.Loadouts != null)
            {
                bool first = true;
                foreach (var kv in g.Loadouts) { if (!first) sb.Append(','); first = false; sb.Append(Json.Str(kv.Key)).Append(':').Append(Json.Str(kv.Value)); }
            }
            sb.Append("},\"p\":"); Params(sb, g.P ?? new CapParams());
            sb.Append(",\"members\":[");
            if (g.Members != null)
                for (int i = 0; i < g.Members.Count; i++)
                {
                    var m = g.Members[i]; if (i > 0) sb.Append(',');
                    sb.Append("{\"uid\":").Append(m.Uid).Append(",\"name\":").Append(Json.Str(m.Name)).Append(",\"type\":").Append(Json.Str(m.Type));
                    sb.Append(",\"slot\":").Append(m.Slot).Append(",\"aam_start\":").Append(m.AamStart).Append(",\"long_start\":").Append(m.LongStart);
                    if (m.LentFrom != null) sb.Append(",\"lent_from\":").Append(Json.Str(m.LentFrom));
                    if (m.LentTo != null) sb.Append(",\"lent_to\":").Append(Json.Str(m.LentTo));
                    if (m.AutoFill) sb.Append(",\"auto_fill\":true");
                    if (m.Deck > 0) sb.Append(",\"deck\":").Append(m.Deck);
                    if (m.TaskDeck > 0) sb.Append(",\"task_deck\":").Append(m.TaskDeck);   // T-218
                    if (m.Where != null) sb.Append(",\"where\":").Append(Json.Str(m.Where));
                    if (m.Loadout != null) sb.Append(",\"loadout\":").Append(Json.Str(m.Loadout));
                    CapAlertBind.Write(sb, m.Bind, missionTimeS);   // T-219
                    sb.Append('}');
                }
            sb.Append("]");
            if (g.Served != null && g.Served.Count > 0) { sb.Append(",\"served\":["); for (int i = 0; i < g.Served.Count; i++) { if (i > 0) sb.Append(','); sb.Append(g.Served[i]); } sb.Append(']'); }
            if (g.AutoName.HasValue) sb.Append(",\"auto_name\":").Append(Json.Bool(g.AutoName.Value));
            if (g.ServedRaids != null && g.ServedRaids.Count > 0) { sb.Append(",\"served_raids\":["); for (int i = 0; i < g.ServedRaids.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Json.Str(g.ServedRaids[i])); } sb.Append(']'); }
            CapAlertBind.WriteMemos(sb, g.Memos, missionTimeS);   // T-219
            sb.Append(",\"keys\":[");
            if (g.Keys != null) for (int i = 0; i < g.Keys.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Json.Str(g.Keys[i])); }
            sb.Append("]}");
        }

        private static void IcptBlock(StringBuilder sb, CapInterceptSpec a)
        {
            sb.Append(",\"intercept\":{\"on\":").Append(Json.Bool(a.On)).Append(",\"margin_nm\":").Append(Json.Num(a.MarginNm)).Append(",\"per_target\":").Append(a.PerTarget).Append(",\"keep\":").Append(a.Keep);
            sb.Append(",\"targets\":[");
            bool first = true;
            foreach (string t in (a.Targets ?? "").Split(',')) { string x = t.Trim(); if (x.Length == 0) continue; if (!first) sb.Append(','); first = false; sb.Append(Json.Str(x)); }
            sb.Append("],\"speed\":").Append(Json.Str(a.Speed ?? "max")).Append(",\"lost_s\":").Append(Json.Num(a.LostS)).Append('}');
        }

        private static CapInterceptSpec ReadIcpt(Dictionary<string, object> o, CapParams p)
        {
            if (o == null) return null;
            var a = CapInterceptSpec.FromParams(p);
            object b; if (o.TryGetValue("on", out b) && b is bool) a.On = (bool)b;
            a.MarginNm = Math.Max(0, Math.Min(50, Num(o, "margin_nm", a.MarginNm)));
            a.PerTarget = Math.Max(1, Math.Min(4, (int)Num(o, "per_target", a.PerTarget)));
            a.Keep = Math.Max(0, Math.Min(50, (int)Num(o, "keep", a.Keep)));
            var tl = Val(o, "targets") as List<object>;
            if (tl != null)
            {
                var parts = new List<string>(); foreach (object x in tl) { var sx = x as string; if (sx != null) parts.Add(sx); }
                bool ok; var t = CapAlertMath.ParseTargets(string.Join(",", parts), out ok); if (ok) a.Targets = CapAlertMath.TargetsCsv(t);
            }
            string v = Str(o, "speed"); if (v == "max" || v == "cruise") a.Speed = v;
            a.LostS = Math.Max(5, Math.Min(600, Num(o, "lost_s", a.LostS)));
            return a;
        }

        private static void AlertBlock(StringBuilder sb, CapAlertSpec a)
        {
            sb.Append(",\"alert\":{\"ref\":").Append(a.RefUid).Append(",\"ready\":").Append(a.Ready).Append(",\"per_raid\":").Append(a.PerRaid);
            sb.Append(",\"level\":").Append(Json.Str(a.Level)).Append(",\"radius_nm\":").Append(Json.Num(a.RadiusNm)).Append(",\"targets\":[");
            string csv = CapAlertMath.TargetsCsv(a.Targets);
            if (csv.Length > 0) { bool first = true; foreach (string t in csv.Split(',')) { if (!first) sb.Append(','); first = false; sb.Append(Json.Str(t)); } }
            sb.Append("],\"pursuit_nm\":").Append(Json.Num(a.PursuitNm)).Append(",\"speed\":").Append(Json.Str(a.Speed)).Append(",\"roe\":").Append(Json.Str(a.Roe));
            sb.Append(",\"after\":").Append(Json.Str(a.After)).Append(",\"fill\":").Append(Json.Bool(a.Fill)).Append('}');
        }

        private static void Params(StringBuilder sb, CapParams p)
        {
            sb.Append("{\"missile_pct\":").Append(Json.Num(p.MissilePct)).Append(",\"fuel_margin_s\":").Append(Json.Num(p.FuelMarginS)).Append(",\"ring_margin_nm\":").Append(Json.Num(p.RingMarginNm));
            sb.Append(",\"fuel_floor_pct\":").Append(Json.Num(p.FuelFloorPct)).Append(",\"combat_hostile_nm\":").Append(Json.Num(p.CombatHostileNm));
            sb.Append(",\"pursuit_nm\":").Append(Json.Num(p.PursuitNm)).Append(",\"threat_nm\":").Append(Json.Num(p.ThreatNm));
            sb.Append(",\"threat_clear_margin_nm\":").Append(Json.Num(p.ThreatClearMarginNm)).Append(",\"threat_clear_s\":").Append(Json.Num(p.ThreatClearS));
            sb.Append(",\"launch_delay_s\":").Append(Json.Num(p.LaunchDelayS)).Append(",\"patrol_alt_ft\":").Append(Json.Num(p.PatrolAltFt));
            sb.Append(",\"lane_end_max_nm\":").Append(Json.Num(p.LaneEndMaxNm)).Append(",\"cover_aam_frac\":").Append(Json.Num(p.CoverAamFrac)).Append(",\"cover_fallback_nm\":").Append(Json.Num(p.CoverFallbackNm)).Append(",\"circle_alt_step_ft\":").Append(Json.Num(p.CircleAltStepFt));
            sb.Append(",\"orbit_below_nm\":").Append(Json.Num(p.OrbitBelowNm)).Append(",\"default_on_station\":").Append(p.DefaultOnStation);
            sb.Append(",\"need_long_range\":").Append(Json.Bool(p.NeedLongRange));
            sb.Append(",\"roe\":").Append(Json.Str(p.Roe)).Append(",\"loadout_tie_break\":").Append(Json.Str(p.LoadoutTieBreak));
            sb.Append(",\"launch_mission_type\":").Append(Json.Str(p.LaunchMissionType));
            sb.Append(",\"air_only\":").Append(Json.Bool(p.AirOnly)).Append(",\"rtb_hold\":").Append(Json.Bool(p.RtbRoeHold)).Append('}');
        }

        private static void Mem(StringBuilder sb, CapMemDto m)
        {
            sb.Append("{\"uid\":").Append(m.Uid).Append(",\"roe\":").Append(Json.Str(m.Roe)).Append(",\"route\":[");
            if (m.Route != null)
                for (int i = 0; i < m.Route.Count; i++) { if (i > 0) sb.Append(','); Arr(sb, m.Route[i]); }
            sb.Append("],\"throttle\":").Append(m.Throttle).Append(",\"dash\":").Append(Json.Bool(m.Dash));
            if (m.RadarOnly) sb.Append(",\"radar_only\":true");
            if (m.Radar != null) sb.Append(",\"radar\":").Append(AirRadarRules.ToJson(m.Radar));   // T-241
            sb.Append('}');
        }

        // ---- reader (tolerant: unknown fields ignored, missing fields keep defaults) --------------------------------------------

        public static CapSaveData Parse(string json, out string err)
        {
            err = null;
            var r = new CapSaveData();
            if (string.IsNullOrWhiteSpace(json)) return r;
            try
            {
                var root = Json.Parse(json) as Dictionary<string, object>;
                if (root == null) { err = "not an object"; return new CapSaveData(); }
                r.Format = (int)Num(root, "format", CurrentFormat);
                if (r.Format > CurrentFormat) { err = "format " + r.Format + " is newer than " + CurrentFormat; return new CapSaveData(); }
                r.MissionTimeS = Num(root, "mission_time_s", 0);
                r.Sav = SavStamp.FromJson(Val(root, "sav"));
                foreach (var go in List(root, "groups"))
                {
                    var g = go as Dictionary<string, object>; if (g == null) continue;
                    var d = new CapGroupDto
                    {
                        Group = Str(g, "group"), Seq = (int)Num(g, "seq", 0), OnStation = (int)Num(g, "on_station", 0), Ready = (int)Num(g, "ready", 0),
                        Roe = Str(g, "roe"), Anchor = Str(g, "anchor") ?? "geo", AnchorUid = (int)Num(g, "anchor_uid", 0),
                        OffLat = Num(g, "off_lat", 0), OffLon = Num(g, "off_lon", 0),
                        Loadouts = new Dictionary<string, string>(), Members = new List<CapMemberDto>(), Keys = new List<string>(), P = new CapParams(),
                    };
                    d.Rect = Doubles(Val(g, "rect"));
                    bool hasZone = d.Rect != null && d.Rect.Length >= 4;
                    var lo = Val(g, "loadouts") as Dictionary<string, object>;
                    if (lo != null) foreach (var kv in lo) { var s = kv.Value as string; if (s != null) d.Loadouts[kv.Key] = s; }
                    ReadParams(Val(g, "p") as Dictionary<string, object>, d.P);
                    if (r.Format >= 2)
                    {
                        d.Kind = Str(g, "kind") == "alert" ? "alert" : "patrol";
                        object ac; if (g.TryGetValue("accept_alert", out ac) && ac is bool) d.AcceptAlert = (bool)ac;
                        var ab = Val(g, "alert") as Dictionary<string, object>;
                        if (d.Kind == "alert") d.AlertSpec = ReadAlert(ab, d.P);
                        else d.Icpt = ReadIcpt(Val(g, "intercept") as Dictionary<string, object>, d.P);   // T-333: no block = null (the [Cap] defaults)
                    }
                    else
                    {
                        // format 1 (ADR-0079 section 11): "alert" N = the alert pair inside the group. Without a zone it becomes an alert group, with a zone it is dropped
                        int old = (int)Num(g, "alert", 0);
                        d.Migrated = CapAlertMath.MigrateV1(hasZone, old);
                        if (d.Migrated == "none") d.Migrated = null;
                        else if (d.Migrated == "alert_group")
                        {
                            d.Kind = "alert"; d.AlertSpec = CapAlertSpec.FromParams(d.P);
                            d.AlertSpec.Ready = Math.Max(1, Math.Min(4, old)); d.AlertSpec.RefUid = (int)Num(g, "alert_ref", 0);
                        }
                    }
                    foreach (var mo in List(g, "members"))
                    {
                        var m = mo as Dictionary<string, object>; if (m == null) continue;
                        var md = new CapMemberDto
                        {
                            Uid = (int)Num(m, "uid", 0), Name = Str(m, "name"), Type = Str(m, "type"), Slot = (int)Num(m, "slot", -1),
                            AamStart = (int)Num(m, "aam_start", 0), LongStart = (int)Num(m, "long_start", 0), LentFrom = Str(m, "lent_from"), LentTo = Str(m, "lent_to"), Deck = (int)Num(m, "deck", 0),
                            TaskDeck = (int)Num(m, "task_deck", 0), Where = Str(m, "where"), Loadout = Str(m, "loadout"),
                        };
                        object af; if (m.TryGetValue("auto_fill", out af) && af is bool) md.AutoFill = (bool)af;
                        md.Bind = CapAlertBind.Read(m, r.MissionTimeS);   // T-219
                        d.Members.Add(md);
                    }
                    foreach (var k in List(g, "served")) { if (k is double) { if (d.Served == null) d.Served = new List<int>(); d.Served.Add((int)(double)k); } }
                    object an; if (g.TryGetValue("auto_name", out an) && an is bool) d.AutoName = (bool)an;
                    foreach (var k in List(g, "served_raids")) { var s = k as string; if (s != null) { if (d.ServedRaids == null) d.ServedRaids = new List<string>(); d.ServedRaids.Add(s); } }
                    d.Memos = CapAlertBind.ReadMemos(g, r.MissionTimeS);   // T-219
                    foreach (var k in List(g, "keys")) { var s = k as string; if (s != null) d.Keys.Add(s); }
                    r.Groups.Add(d);
                }
                foreach (var mo in List(root, "memory"))
                {
                    var m = mo as Dictionary<string, object>; if (m == null) continue;
                    var md = new CapMemDto { Uid = (int)Num(m, "uid", 0), Roe = Str(m, "roe"), Route = new List<double[]>(), Throttle = (int)Num(m, "throttle", -1) };
                    object dv; if (m.TryGetValue("dash", out dv) && dv is bool) md.Dash = (bool)dv;
                    if (m.TryGetValue("radar_only", out dv) && dv is bool) md.RadarOnly = (bool)dv;   // T-241
                    md.Radar = AirRadarRules.FromJson(Val(m, "radar") as Dictionary<string, object>);
                    foreach (var po in List(m, "route")) { var a = Doubles(po); if (a != null) md.Route.Add(a); }
                    r.Memory.Add(md);
                }
            }
            catch (Exception e)
            {
                err = "parse: " + e.Message;
                return new CapSaveData();
            }
            return r;
        }

        private static CapAlertSpec ReadAlert(Dictionary<string, object> o, CapParams p)
        {
            var a = CapAlertSpec.FromParams(p);
            if (o == null) return a;
            a.RefUid = (int)Num(o, "ref", 0); a.Ready = Math.Max(1, Math.Min(4, (int)Num(o, "ready", a.Ready)));
            a.PerRaid = Math.Max(0, Math.Min(4, (int)Num(o, "per_raid", a.PerRaid)));
            string v = Str(o, "level"); if (v == "deck" || v == "hangar") a.Level = v;
            a.RadiusNm = Num(o, "radius_nm", a.RadiusNm); a.PursuitNm = Num(o, "pursuit_nm", a.PursuitNm);
            var tl = Val(o, "targets") as List<object>;
            if (tl != null)
            {
                var parts = new List<string>(); foreach (object x in tl) { var sx = x as string; if (sx != null) parts.Add(sx); }
                bool ok; var t = CapAlertMath.ParseTargets(string.Join(",", parts), out ok); if (ok) a.Targets = t;
            }
            v = Str(o, "speed"); if (v == "max" || v == "cruise") a.Speed = v;
            v = Str(o, "roe"); if (v == "tight" || v == "free" || v == "hold") a.Roe = v;
            v = Str(o, "after"); if (v == "alert" || v == "patrol") a.After = v;
            object f; if (o.TryGetValue("fill", out f) && f is bool) a.Fill = (bool)f;
            return a;
        }

        private static void ReadParams(Dictionary<string, object> o, CapParams p)
        {
            if (o == null) return;
            p.MissilePct = Num(o, "missile_pct", p.MissilePct); p.FuelMarginS = Num(o, "fuel_margin_s", p.FuelMarginS); p.RingMarginNm = Num(o, "ring_margin_nm", p.RingMarginNm);
            p.FuelFloorPct = Num(o, "fuel_floor_pct", p.FuelFloorPct); p.CombatHostileNm = Num(o, "combat_hostile_nm", p.CombatHostileNm);
            p.PursuitNm = Num(o, "pursuit_nm", p.PursuitNm); p.ThreatNm = Num(o, "threat_nm", p.ThreatNm);
            p.ThreatClearMarginNm = Num(o, "threat_clear_margin_nm", p.ThreatClearMarginNm); p.ThreatClearS = Num(o, "threat_clear_s", p.ThreatClearS);
            p.LaunchDelayS = Num(o, "launch_delay_s", p.LaunchDelayS); p.PatrolAltFt = Num(o, "patrol_alt_ft", p.PatrolAltFt);
            p.LaneEndMaxNm = Num(o, "lane_end_max_nm", p.LaneEndMaxNm);   // T-235: the old lane_sep_max_nm is ignored
            p.CoverAamFrac = Num(o, "cover_aam_frac", p.CoverAamFrac); p.CoverFallbackNm = Num(o, "cover_fallback_nm", p.CoverFallbackNm);
            p.CircleAltStepFt = Num(o, "circle_alt_step_ft", p.CircleAltStepFt);
            p.OrbitBelowNm = Num(o, "orbit_below_nm", p.OrbitBelowNm); p.DefaultOnStation = (int)Num(o, "default_on_station", p.DefaultOnStation);
            object b; if (o.TryGetValue("need_long_range", out b) && b is bool) p.NeedLongRange = (bool)b;
            if (o.TryGetValue("air_only", out b) && b is bool) p.AirOnly = (bool)b;
            if (o.TryGetValue("rtb_hold", out b) && b is bool) p.RtbRoeHold = (bool)b;
            p.Roe = Str(o, "roe") ?? p.Roe; p.LoadoutTieBreak = Str(o, "loadout_tie_break") ?? p.LoadoutTieBreak;
            p.LaunchMissionType = Str(o, "launch_mission_type") ?? p.LaunchMissionType;
        }

        private static object Val(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v : null; }
        private static string Str(Dictionary<string, object> d, string k) { return Val(d, k) as string; }
        private static double Num(Dictionary<string, object> d, string k, double def) { object v = Val(d, k); return v is double ? (double)v : def; }
        private static List<object> List(Dictionary<string, object> d, string k) { return Val(d, k) as List<object> ?? new List<object>(); }

        private static double[] Doubles(object o)
        {
            var l = o as List<object>; if (l == null) return null;
            var a = new double[l.Count];
            for (int i = 0; i < a.Length; i++) { if (!(l[i] is double)) return null; a[i] = (double)l[i]; }
            return a;
        }

        // ---- sample + selftest --------------------------------------------------------------------------------------------------

        private static CapSaveData SampleData()
        {
            var d = new CapSaveData { MissionTimeS = 1234.5 };
            var g1 = new CapGroupDto
            {
                Group = "ПВО-1", Seq = 1, OnStation = 2, Ready = 1, Roe = "tight", Rect = new[] { 35.0, 20.0, 35.3, 20.7 },
                Anchor = "geo", Loadouts = new Dictionary<string, string> { { "F-14A", "AirToAirLongRange" }, { "F-4J", "auto" } },
                P = new CapParams { MissilePct = 0.3, RingMarginNm = 7, NeedLongRange = true },
                Icpt = new CapInterceptSpec { On = false, MarginNm = 15, PerTarget = 3, Keep = 1, Targets = "air", Speed = "cruise", LostS = 90 },
                Members = new List<CapMemberDto>
                {
                    new CapMemberDto { Uid = 101, Name = "Viking 101", Type = "F-14A", Slot = 0, AamStart = 8, LongStart = 6 },
                    new CapMemberDto { Uid = 102, Name = "Viking 102", Type = "F-14A", Slot = -1, AamStart = 8, LongStart = 6, Deck = 77 },
                },
                Keys = new List<string> { "task-17", "task-18" },
            };
            var g2 = new CapGroupDto
            {
                Group = "ПВО-2", Seq = 3, OnStation = 1, Ready = 0, Roe = "free", Rect = new[] { 36.0, 21.0, 36.2, 21.5 },
                Anchor = "unit", AnchorUid = 55, OffLat = 0.05, OffLon = -0.1, Loadouts = new Dictionary<string, string>(),
                P = new CapParams(), Members = new List<CapMemberDto> { new CapMemberDto { Uid = 201, Name = "Sparrow", Type = "F-4J", Slot = 0, AamStart = 4 } },
                Keys = new List<string>(),
            };
            g1.Members[1].LentFrom = "ПВО-3"; g1.Members[1].AutoFill = true; g1.AcceptAlert = false;
            var g3 = new CapGroupDto
            {
                Group = "ПВО-3", Seq = 4, OnStation = 1, Ready = 0, Roe = "tight", Rect = null, Anchor = "geo", Loadouts = new Dictionary<string, string>(), P = new CapParams(),
                Kind = "alert",
                AlertSpec = new CapAlertSpec { RefUid = 200, Ready = 3, PerRaid = 0, Level = "hangar", RadiusNm = 100, PursuitNm = 80, Speed = "cruise", Roe = "hold", After = "alert", Fill = true, Targets = AlertTargets.Air | AlertTargets.Helo },
                Members = new List<CapMemberDto> { new CapMemberDto { Uid = 301, Name = "Tom", Type = "F-14A", Slot = -1 }, new CapMemberDto { Uid = 302, Name = "Tom2", Type = "F-14A", LentTo = "ПВО-1" } },
                Keys = new List<string>(),
                Served = new List<int> { 7027, 7029 }, ServedRaids = new List<string> { "r7026" },
            };
            g3.Members[0].Bind = new CapAlertBindDto
            {
                State = "scramble", Raid = "7027", Key = "r7027", RaidIds = new List<int> { 7027 }, Barrage = false, Handed = false,
                Tgt = new RaidMemo { Lat = 35.5, Lon = 20.25, CourseDeg = 180, Kn = 450, At = 1200.5 },
            };
            g3.Memos = new List<RaidMemo> { new RaidMemo { Key = "r7027", Ids = new List<int> { 7027 }, Lat = 35.6, Lon = 20.3, CourseDeg = 170, Kn = 480, At = 1204.5 } };
            d.Groups.Add(g1); d.Groups.Add(g2); d.Groups.Add(g3);
            d.Memory.Add(new CapMemDto { Uid = 101, Roe = "free", Route = new List<double[]> { new[] { 35.1, 20.1 }, new[] { 35.2, 20.3 } } });
            return d;
        }

        public static string Sample() { return ToJson(SampleData()); }

        /// <summary>null = ok, otherwise error text.</summary>
        public static string SelfTest()
        {
            string err;
            var d = SampleData();
            string j = ToJson(d);
            var b = Parse(j, out err);
            if (err != null) return "roundtrip err " + err;
            if (ToJson(b) != j) return "roundtrip differs";
            {   // T-198: the optional "sav" stamp
                string jn = ToJson(d);
                if (jn.Contains("\"sav\"") || b.Sav != null) return "sav must be absent without a stamp";
                d.Sav = SavStamp.Now(2425.5, new DateTime(2026, 10, 23, 2, 7, 37, DateTimeKind.Utc), "hook");
                string js = ToJson(d);
                var bs = Parse(js, out err);
                if (err != null || bs.Sav == null || Math.Abs(bs.Sav.ElapsedS - 2425.5) > 1e-9 || bs.Sav.Created != "2026-10-23T02:07:37Z" || bs.Sav.Src != "hook" || bs.Format != 3 || ToJson(bs) != js) return "sav round trip";
                d.Sav = null;
            }
            if (b.Format != 3 || b.Groups.Count != 3 || b.Groups[2].Kind != "alert" || b.Groups[0].Kind != "patrol") return "kind";
            var sp = b.Groups[2].AlertSpec;
            if (sp == null || sp.RefUid != 200 || sp.Ready != 3 || sp.PerRaid != 0 || sp.Level != "hangar" || sp.RadiusNm != 100 || sp.PursuitNm != 80 || sp.Speed != "cruise" || sp.Roe != "hold" || sp.After != "alert" || !sp.Fill
                || sp.Targets != (AlertTargets.Air | AlertTargets.Helo)) return "alert spec";
            var ib = b.Groups[0].Icpt;   // T-333
            if (ib == null || ib.On || ib.MarginNm != 15 || ib.PerTarget != 3 || ib.Keep != 1 || ib.Targets != "air" || ib.Speed != "cruise" || ib.LostS != 90 || b.Groups[1].Icpt != null || b.Groups[2].Icpt != null) return "intercept block";
            if (!j.Contains("\"intercept\":{\"on\":false,\"margin_nm\":15")) return "intercept block keys";
            if (b.Groups[0].AcceptAlert || !b.Groups[1].AcceptAlert || b.Groups[0].Members[1].LentFrom != "ПВО-3" || !b.Groups[0].Members[1].AutoFill || b.Groups[2].Members[1].LentTo != "ПВО-1") return "accept / lent / auto";
            if (!j.Contains("\"kind\":\"alert\"") || !j.Contains("\"targets\":[\"air\",\"helo\"]") || !j.Contains("\"accept_alert\":false")) return "format 3 keys";
            // format 1 (T-179, ADR-0079 section 11): "alert" without a zone = an alert group, with a zone = dropped
            var v1 = Parse("{\"format\":1,\"groups\":[{\"group\":\"A\",\"alert\":2,\"alert_ref\":200,\"rect\":[],\"members\":[{\"uid\":1}]},"
                + "{\"group\":\"B\",\"alert\":2,\"alert_ref\":200,\"rect\":[35,20,35.3,20.7]},{\"group\":\"C\",\"rect\":[35,20,35.3,20.7]}]}", out err);
            if (err != null || v1.Groups.Count != 3) return "format 1 read " + err;
            if (v1.Groups[0].Kind != "alert" || v1.Groups[0].Migrated != "alert_group" || v1.Groups[0].AlertSpec.Ready != 2 || v1.Groups[0].AlertSpec.RefUid != 200) return "format 1 -> alert group";
            if (v1.Groups[1].Kind != "patrol" || v1.Groups[1].Migrated != "dropped" || v1.Groups[1].AlertSpec != null) return "format 1 -> dropped";
            if (v1.Groups[2].Kind != "patrol" || v1.Groups[2].Migrated != null) return "format 1 plain patrol";
            if (b.Groups.Count != 3 || b.Groups[0].Members.Count != 2 || b.Groups[0].Members[0].AamStart != 8 || b.Groups[0].Members[1].Slot != -1) return "members";
            if (b.Groups[1].Anchor != "unit" || b.Groups[1].AnchorUid != 55 || Math.Abs(b.Groups[1].OffLon + 0.1) > 1e-12) return "unit anchor";
            if (b.Groups[0].Loadouts["F-14A"] != "AirToAirLongRange" || b.Groups[0].Keys.Count != 2 || !b.Groups[0].P.NeedLongRange || b.Groups[0].P.MissilePct != 0.3 || b.Groups[0].P.RingMarginNm != 7 || b.Groups[1].P.RingMarginNm != 5) return "loadouts/keys/params";
            if (b.Memory.Count != 1 || b.Memory[0].Route.Count != 2 || b.Memory[0].Roe != "free") return "memory";
            var oldp = Parse("{\"format\":1,\"groups\":[{\"group\":\"G\",\"p\":{\"missile_pct\":0.3}}]}", out err);   // T-330: an old save without ring_margin_nm keeps the cfg value
            if (err != null || oldp.Groups.Count != 1 || oldp.Groups[0].P.RingMarginNm != 5 || oldp.Groups[0].P.MissilePct != 0.3) return "old params " + err;
            var x = Parse("{\"format\":1,\"extra\":{\"a\":1},\"groups\":[{\"group\":\"G\",\"zzz\":5}],\"memory\":[]}", out err);
            if (err != null || x.Groups.Count != 1 || x.Groups[0].Group != "G") return "unknown fields";
            if (b.Groups[0].Members[1].Deck != 77 || b.Groups[0].Members[0].Deck != 0 || !j.Contains("\"deck\":77") || j.Contains("\"deck\":0")) return "member deck (T-186)";
            var f2 = Parse("{\"format\":2,\"groups\":[{\"group\":\"G\",\"members\":[{\"uid\":-2000101,\"type\":\"F-14A\"}]}]}", out err);
            if (err != null || f2.Format != 2 || f2.Groups[0].Members[0].Uid != -2000101 || f2.Groups[0].Members[0].Deck != 0) return "format 2 still readable";
            if (b.Groups[2].Served == null || b.Groups[2].Served.Count != 2 || b.Groups[2].Served[0] != 7027 || b.Groups[2].ServedRaids == null || b.Groups[2].ServedRaids[0] != "r7026" || !j.Contains("\"served\":[7027,7029]") || !j.Contains("\"served_raids\":[\"r7026\"]")) return "served round trip (T-211)";
            if (b.Groups[0].Served != null || b.Groups[0].ServedRaids != null || f2.Groups[0].Served != null) return "no served without the field (T-211)";
            {   // T-218: task_deck / where / loadout of a deck member on a task row (optional, format stays 3)
                var t = SampleData(); t.Groups[0].Members[0].TaskDeck = 77; t.Groups[0].Members[0].Where = "deck_ready"; t.Groups[0].Members[0].Loadout = "AirToAirLongRange";
                string jt = ToJson(t); var bt = Parse(jt, out err); var mt = err == null ? bt.Groups[0].Members[0] : null;
                if (mt == null || mt.TaskDeck != 77 || mt.Where != "deck_ready" || mt.Loadout != "AirToAirLongRange" || bt.Format != 3 || ToJson(bt) != jt) return "task_deck round trip (T-218)";
                if (j.Contains("task_deck") || j.Contains("\"where\"") || j.Contains("\"loadout\"") || b.Groups[0].Members[0].TaskDeck != 0 || b.Groups[0].Members[0].Where != null) return "no task_deck keys without the fields (T-218)";
            }
            {   // T-219 (ADR-0090): alert_bind of a member and served_memo of a group (optional, format stays 3)
                var tb = b.Groups[2].Members[0].Bind;
                if (tb == null || tb.State != "scramble" || tb.Raid != "7027" || tb.Key != "r7027" || tb.RaidIds == null || tb.RaidIds[0] != 7027 || tb.Tgt == null || Math.Abs(tb.Tgt.At - 1200.5) > 1e-6) return "alert_bind round trip (T-219)";
                var tm = b.Groups[2].Memos;
                if (tm == null || tm.Count != 1 || tm[0].Key != "r7027" || tm[0].Ids[0] != 7027 || Math.Abs(tm[0].At - 1204.5) > 1e-6 || b.Format != 3) return "served_memo round trip (T-219)";
                if (!j.Contains("\"alert_bind\"") || !j.Contains("\"served_memo\"") || b.Groups[0].Members[0].Bind != null || b.Groups[0].Memos != null || f2.Groups[0].Members[0].Bind != null || f2.Groups[0].Memos != null) return "no alert_bind without the field (T-219)";
            }
            {   // T-326: optional "auto_name"
                var t = SampleData(); t.Groups[0].AutoName = false; t.Groups[1].AutoName = true; t.Groups[2].Group = "Конвой Север";
                string ja = ToJson(t); var ba = Parse(ja, out err);
                if (err != null || ba.Groups[0].AutoName != false || ba.Groups[1].AutoName != true || ba.Groups[2].AutoName != null || ba.Groups[2].Group != "Конвой Север" || ToJson(ba) != ja) return "auto_name round trip (T-326)";
                if (j.Contains("auto_name") || b.Groups[0].AutoName != null) return "no auto_name without the field (T-326)";
            }
            Parse("{\"format\":4,\"groups\":[]}", out err);
            if (err == null) return "newer format must give err";
            var e = Parse("", out err);
            if (err != null || e.Groups.Count != 0 || e.Memory.Count != 0) return "empty string";
            Parse("{not json", out err);
            if (err == null) return "broken json must give err";
            return null;
        }
    }
}
