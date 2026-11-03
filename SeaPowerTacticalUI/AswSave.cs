using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VoiceBotBridge
{
    // T-095 / ADR-0036 section 9: the ASW state file kept next to the game save. Pure, built on Common/Json.cs.

    internal sealed class AswMemberRef { public int Uid; public string Name, Type; public string Label; /* not saved: display class name, matches saves made before T-142 */ public int Deck; /* T-181: ship uid of a hangar slot member, 0 = none; saved as "deck" only when != 0 */ }

    internal sealed class AswGroupDto
    {
        public string Name, Order = "none", TargetName, Depth;
        public int TargetUid;
        public string TargetKind = "ship";                                  // T-142 K: ship | formation (the course follows the leader)
        public Dictionary<string, string> Loadouts = new Dictionary<string, string>();   // T-142 K: vehicle type -> chosen loadout ("auto" is not saved)
        public bool? AutoName;          // T-150: the name is made by the mod; null = old file (decided from the name)
        public int Seq;                 // T-119: shared creation number (AirOwnership.GroupSeq), 0 = not saved (old file)
        public List<string> Tactics = new List<string>();
        public AswCourseSpec Course;     // T-176: the v3 by-course order; null = an old file (the controller migrates the tactics)
        public string AreaPattern;       // T-176: auto | grid | dip | mad; null = an old file
        public AswBarrierBandDto Barrier;   // T-229: the band of the sliding barrier; null = none (an old file, or no band yet: it is built from the ship)
        public double? FrameAxisDeg;        // T-238: the axis of the anchor when saved (course order); null = an old file / no anchor
        public double[] Rect;
        // T-173 (ADR-0074): the strike group's order and the list "do not attack"; written only for order "strike", an old file has none
        public int RefUid, Airborne;
        public double RadiusNm;
        public string Targets;
        public List<int> Skip = new List<int>();
        public AswParams Params = new AswParams();
        public List<AswMemberRef> Members = new List<AswMemberRef>();
    }

    /// <summary>T-229: one row of the sliding barrier in the file (Open = the points not given to an aircraft yet, [lat, lon]).</summary>
    internal sealed class AswBarrierRowDto { public int Seq, Buoys; public double Lat, Lon, AxisDeg, HalfNm, XNm, LaidS; public bool Passed; public List<double[]> Open = new List<double[]>(); }

    /// <summary>T-229: the band of the sliding barrier: origin, axis, the next row number and the rows ahead (and the passed rows not forgotten yet).</summary>
    internal sealed class AswBarrierBandDto { public double Lat, Lon, AxisDeg, T0; public int NextSeq; public List<AswBarrierRowDto> Rows = new List<AswBarrierRowDto>(); }

    internal sealed class AswBuoyDto { public double Lat, Lon, DroppedS, LifeS; public string Ammo, Group; }
    internal sealed class AswContactDto { public int Id; public string Name, Group; public double Lat, Lon, SeenS; }
    internal sealed class AswMemDto { public int Uid; public string Name, Type, Roe, Depth; public AirRadarDto Radar; /* T-241 (ADR-0099): radar memory of the aircraft, JSON "radar"; null = none / old file */ }

    internal sealed class AswSaveData
    {
        public int Format = 2;                 // 2 (T-109): adds the "sar" section; 1 is still read
        public double MissionTimeS;
        public string SavePath;
        public SavStamp Sav;                   // T-198 (ADR-0086): stamp of the game save the file was written for; null = old file
        public List<AswGroupDto> Groups = new List<AswGroupDto>();
        public List<AswBuoyDto> Buoys = new List<AswBuoyDto>();
        public List<AswContactDto> Contacts = new List<AswContactDto>();
        public List<AswMemDto> Memory = new List<AswMemDto>();
        public SarSaveData Sar = new SarSaveData();   // section "sar" (ADR-0039 section 10); empty for a format 1 file
    }

    internal static class AswSave
    {
        public static string Key(string savPath) { return SaveSlotStore.Key(savPath); }

        public static bool Fresh(AswSaveData d, double missionTimeS, double tolS = 5)
        {
            return d != null && Math.Abs(d.MissionTimeS - missionTimeS) <= tolS;
        }

        /// <summary>Index into <paramref name="live"/>: by uid first, then by Name+Type; -1 if none.</summary>
        public static int Match(AswMemberRef r, IList<AswMemberRef> live)
        {
            return Match(r, live, new HashSet<int>(), new HashSet<int>());
        }

        /// <summary>T-255: index into live by uid first, then by Name+Type; a live uid in taken is skipped,
        /// and the Name fallback never takes a live uid that is the saved uid of a member of the file. -1 = none.</summary>
        public static int Match(AswMemberRef r, IList<AswMemberRef> live, ICollection<int> taken, ICollection<int> fileUids)
        {
            for (int i = 0; i < live.Count; i++) if (live[i].Uid == r.Uid && SameType(r, live[i]) && !taken.Contains(live[i].Uid)) return i;
            for (int i = 0; i < live.Count; i++)
                if (live[i].Name == r.Name && SameType(r, live[i]) && !taken.Contains(live[i].Uid) && (live[i].Uid == r.Uid || !fileUids.Contains(live[i].Uid))) return i;
            return -1;
        }

        /// <summary>T-252: the memory record of a restored member, by its saved uid first; each record is taken once.
        /// Fallback by Name+Type only to a record whose uid is not the saved uid of any member of the file (old files, uid changed). -1 = none.</summary>
        public static int MatchMemory(AswMemberRef saved, IList<AswMemDto> memory, ICollection<int> taken, ICollection<int> fileUids)
        {
            bool uidSeen = false;
            for (int i = 0; i < memory.Count; i++)
            {
                if (memory[i].Uid != saved.Uid) continue;
                uidSeen = true;
                if (!taken.Contains(i) && MemSameType(saved, memory[i])) return i;
            }
            if (uidSeen) return -1;   // a (duplicate) record of this uid exists: never given to a neighbour by name
            for (int i = 0; i < memory.Count; i++)
                if (!taken.Contains(i) && !fileUids.Contains(memory[i].Uid) && memory[i].Name == saved.Name && MemSameType(saved, memory[i])) return i;
            return -1;
        }

        private static bool MemSameType(AswMemberRef saved, AswMemDto m)
        {
            return m.Type == saved.Type || (saved.Label != null && saved.Label == m.Type);
        }

        /// <summary>Saves before T-142 hold the aircraft ClassName as the type; now the type key is the ini name and the ClassName is the label.</summary>
        private static bool SameType(AswMemberRef r, AswMemberRef live)
        {
            return live.Type == r.Type || (live.Label != null && live.Label == r.Type);
        }

        // ---- writer ------------------------------------------------------------------------------------------------

        private static string N(double d) { return Json.Num(d); }

        private static string LoadoutsJson(IDictionary<string, string> l)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            if (l != null)
                foreach (var kv in l)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value) || string.Equals(kv.Value, "auto", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!first) sb.Append(','); first = false;
                    sb.Append(Json.Str(kv.Key)).Append(':').Append(Json.Str(kv.Value));
                }
            return sb.Append('}').ToString();
        }

        private static Dictionary<string, string> LoadoutsFrom(Dictionary<string, object> g)
        {
            var res = new Dictionary<string, string>();
            object o;
            var d = g.TryGetValue("loadouts", out o) ? o as Dictionary<string, object> : null;
            if (d != null) foreach (var kv in d) { var v = kv.Value as string; if (v != null && v.Length > 0) res[kv.Key] = v; }
            return res;
        }

        private static string Arr<T>(IList<T> l, Func<T, string> f)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(','); sb.Append(f(l[i])); }
            return sb.Append(']').ToString();
        }

        private static string ParamsJson(AswParams p)
        {
            return "{\"length_nm\":" + N(p.LengthNm) + ",\"width_nm\":" + N(p.WidthNm) + ",\"a_new_nm\":" + N(p.ANewNm) + ",\"d_min_nm\":" + N(p.DMinNm) +
                   ",\"lead_mad_nm\":" + N(p.LeadMadNm) + ",\"lead_dip_nm\":" + N(p.LeadDipNm) + ",\"dip_hold_s\":" + N(p.DipHoldS) +
                   ",\"mad_leg_nm\":" + N(p.MadLegNm) + ",\"area_size_nm\":" + N(p.AreaSizeNm) + ",\"game_estimate_factor\":" + N(p.GameEstimateFactor) +
                   ",\"ini_factor\":" + N(p.IniFactor) + ",\"active_factor\":" + N(p.ActiveFactor) + ",\"r_min_nm\":" + N(p.RMinNm) +
                   ",\"r_max_nm\":" + N(p.RMaxNm) + ",\"lost_after_s\":" + N(p.LostAfterS) + ",\"turnaround_s\":" + N(p.TurnaroundS) +
                   ",\"standby\":" + p.Standby.ToString(CultureInfo.InvariantCulture) + ",\"loadouts\":" + Json.Str(p.Loadouts) + "}";
        }

        private static string CourseJson(AswCourseSpec c)
        {
            return "{\"tactics\":" + Arr(c.Tactics, Json.Str) + ",\"corridor_x_nm\":" + N(c.CorridorXNm) + ",\"barrier_x_nm\":" + N(c.BarrierXNm) + ",\"dip_l_nm\":" + N(c.DipLNm) +
                   ",\"corridor_l_nm\":" + N(c.CorridorLNm) + ",\"barrier_l_nm\":" + N(c.BarrierLNm) + ",\"barrier_rows\":" + c.BarrierRows.ToString(CultureInfo.InvariantCulture) + ",\"barrier_gap_nm\":" + N(c.BarrierGapNm) + ",\"depth\":" + Json.Str(c.Depth ?? "auto") + "}";
        }

        private static string BandJson(AswBarrierBandDto b)
        {
            return "{\"lat\":" + N(b.Lat) + ",\"lon\":" + N(b.Lon) + ",\"axis_deg\":" + N(b.AxisDeg) + ",\"t0\":" + N(b.T0) + ",\"next_seq\":" + b.NextSeq.ToString(CultureInfo.InvariantCulture) +
                   ",\"rows\":" + Arr(b.Rows, r => "{\"seq\":" + r.Seq.ToString(CultureInfo.InvariantCulture) + ",\"lat\":" + N(r.Lat) + ",\"lon\":" + N(r.Lon) + ",\"axis_deg\":" + N(r.AxisDeg) + ",\"half_nm\":" + N(r.HalfNm) +
                       ",\"x_nm\":" + N(r.XNm) + ",\"laid_s\":" + N(r.LaidS) + ",\"buoys\":" + r.Buoys.ToString(CultureInfo.InvariantCulture) + ",\"passed\":" + Json.Bool(r.Passed) +
                       ",\"open\":" + Arr(r.Open, p => "[" + N(p[0]) + "," + N(p[1]) + "]") + "}") + "}";
        }

        private static AswBarrierBandDto BandFrom(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var b = new AswBarrierBandDto { Lat = D(o, "lat"), Lon = D(o, "lon"), AxisDeg = D(o, "axis_deg"), T0 = D(o, "t0"), NextSeq = (int)D(o, "next_seq") };
            foreach (object ro in L(o.ContainsKey("rows") ? o["rows"] : null) ?? new List<object>())
            {
                var r = O(ro); if (r == null) continue;
                var row = new AswBarrierRowDto { Seq = (int)D(r, "seq"), Lat = D(r, "lat"), Lon = D(r, "lon"), AxisDeg = D(r, "axis_deg"), HalfNm = D(r, "half_nm"), XNm = D(r, "x_nm"), LaidS = D(r, "laid_s"), Buoys = (int)D(r, "buoys"),
                                                Passed = r.ContainsKey("passed") && r["passed"] is bool && (bool)r["passed"] };
                foreach (object po in L(r.ContainsKey("open") ? r["open"] : null) ?? new List<object>())
                {
                    var pl = L(po);
                    if (pl != null && pl.Count == 2 && pl[0] is double && pl[1] is double) row.Open.Add(new[] { (double)pl[0], (double)pl[1] });
                }
                b.Rows.Add(row);
            }
            return b;
        }

        private static AswCourseSpec CourseFrom(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var c = new AswCourseSpec();
            foreach (object t in L(o.ContainsKey("tactics") ? o["tactics"] : null) ?? new List<object>()) if (t is string) c.Tactics.Add((string)t);
            c.CorridorXNm = D(o, "corridor_x_nm", c.CorridorXNm); c.BarrierXNm = D(o, "barrier_x_nm", c.BarrierXNm); c.DipLNm = D(o, "dip_l_nm", c.DipLNm);
            c.CorridorLNm = D(o, "corridor_l_nm", c.CorridorLNm); c.BarrierLNm = D(o, "barrier_l_nm", c.BarrierLNm); c.Depth = S(o, "depth") ?? "auto";
            c.BarrierRows = Math.Max(1, Math.Min(5, (int)D(o, "barrier_rows", c.BarrierRows))); c.BarrierGapNm = D(o, "barrier_gap_nm", c.BarrierGapNm);   // T-229: an old file -> 2 / 5
            return c;
        }

        public static string ToJson(AswSaveData d)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"format\":").Append(d.Format.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mission_time_s\":").Append(N(d.MissionTimeS));
            sb.Append(",\"save_path\":").Append(Json.Str(d.SavePath));
            if (d.Sav != null) sb.Append(",\"sav\":").Append(d.Sav.ToJson());
            sb.Append(",\"groups\":").Append(Arr(d.Groups, g =>
                "{\"name\":" + Json.Str(g.Name) + ",\"order\":" + Json.Str(g.Order) + ",\"target_name\":" + Json.Str(g.TargetName) +
                ",\"target_uid\":" + g.TargetUid.ToString(CultureInfo.InvariantCulture) + ",\"target_kind\":" + Json.Str(g.TargetKind ?? "ship") + ",\"loadouts\":" + LoadoutsJson(g.Loadouts) + ",\"seq\":" + g.Seq.ToString(CultureInfo.InvariantCulture) + (g.AutoName.HasValue ? ",\"auto_name\":" + Json.Bool(g.AutoName.Value) : "") + ",\"depth\":" + Json.Str(g.Depth) +
                (g.Order == "strike" ? ",\"strike\":{\"ref_uid\":" + g.RefUid.ToString(CultureInfo.InvariantCulture) + ",\"radius_nm\":" + N(g.RadiusNm) + ",\"targets\":" + Json.Str(g.Targets ?? "hostile_unknown") + ",\"airborne\":" + g.Airborne.ToString(CultureInfo.InvariantCulture) + ",\"skip\":" + Arr(g.Skip, i => i.ToString(CultureInfo.InvariantCulture)) + "}" : "") +
                ",\"tactics\":" + Arr(g.Tactics, Json.Str) + (g.Course != null ? ",\"course\":" + CourseJson(g.Course) : "") + (g.Barrier != null ? ",\"barrier\":" + BandJson(g.Barrier) : "") + (g.FrameAxisDeg.HasValue ? ",\"frame_axis_deg\":" + N(g.FrameAxisDeg.Value) : "") + (g.AreaPattern != null ? ",\"area_pattern\":" + Json.Str(g.AreaPattern) : "") + ",\"rect\":" + (g.Rect == null ? "null" : Arr(g.Rect, N)) +
                ",\"params\":" + ParamsJson(g.Params ?? new AswParams()) +
                ",\"members\":" + Arr(g.Members, m => "{\"uid\":" + m.Uid.ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Json.Str(m.Name) + ",\"type\":" + Json.Str(m.Type) + (m.Deck != 0 ? ",\"deck\":" + m.Deck.ToString(CultureInfo.InvariantCulture) : "") + "}") + "}"));   // T-181: deck only for a hangar slot
            sb.Append(",\"buoys\":").Append(Arr(d.Buoys, b =>
                "{\"lat\":" + N(b.Lat) + ",\"lon\":" + N(b.Lon) + ",\"dropped_s\":" + N(b.DroppedS) + ",\"life_s\":" + N(b.LifeS) +
                ",\"ammo\":" + Json.Str(b.Ammo) + ",\"group\":" + Json.Str(b.Group) + "}"));
            sb.Append(",\"contacts\":").Append(Arr(d.Contacts, c =>
                "{\"id\":" + c.Id.ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Json.Str(c.Name) + ",\"group\":" + Json.Str(c.Group) +
                ",\"lat\":" + N(c.Lat) + ",\"lon\":" + N(c.Lon) + ",\"seen_s\":" + N(c.SeenS) + "}"));
            sb.Append(",\"memory\":").Append(Arr(d.Memory, m =>
                "{\"uid\":" + m.Uid.ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Json.Str(m.Name) + ",\"type\":" + Json.Str(m.Type) +
                ",\"roe\":" + Json.Str(m.Roe) + ",\"depth\":" + Json.Str(m.Depth) + (m.Radar != null ? ",\"radar\":" + AirRadarRules.ToJson(m.Radar) : "") + "}"));
            sb.Append(",\"sar\":").Append(SarSave.ToJson(d.Sar));
            return sb.Append('}').ToString();
        }

        // ---- reader ------------------------------------------------------------------------------------------------

        private static Dictionary<string, object> O(object o) { return o as Dictionary<string, object>; }
        private static List<object> L(object o) { return o as List<object>; }
        private static double D(Dictionary<string, object> d, string k, double def = 0)
        {
            object v; if (d != null && d.TryGetValue(k, out v) && v is double) return (double)v; return def;
        }
        private static string S(Dictionary<string, object> d, string k)
        {
            object v; if (d != null && d.TryGetValue(k, out v)) return v as string; return null;
        }

        private static AswParams ParamsFrom(Dictionary<string, object> o)
        {
            var p = new AswParams();
            if (o == null) return p;
            p.LengthNm = D(o, "length_nm", p.LengthNm); p.WidthNm = D(o, "width_nm", p.WidthNm); p.ANewNm = D(o, "a_new_nm", p.ANewNm);
            p.DMinNm = D(o, "d_min_nm", p.DMinNm); p.LeadMadNm = D(o, "lead_mad_nm", p.LeadMadNm); p.LeadDipNm = D(o, "lead_dip_nm", p.LeadDipNm);
            p.DipHoldS = D(o, "dip_hold_s", p.DipHoldS); p.MadLegNm = D(o, "mad_leg_nm", p.MadLegNm); p.AreaSizeNm = D(o, "area_size_nm", p.AreaSizeNm);
            p.GameEstimateFactor = D(o, "game_estimate_factor", p.GameEstimateFactor); p.IniFactor = D(o, "ini_factor", p.IniFactor);
            p.ActiveFactor = D(o, "active_factor", p.ActiveFactor); p.RMinNm = D(o, "r_min_nm", p.RMinNm); p.RMaxNm = D(o, "r_max_nm", p.RMaxNm);
            p.LostAfterS = D(o, "lost_after_s", p.LostAfterS); p.TurnaroundS = D(o, "turnaround_s", p.TurnaroundS);
            p.Standby = (int)D(o, "standby", p.Standby);
            string lo = S(o, "loadouts"); if (lo != null) p.Loadouts = lo;
            return p;
        }

        public static AswSaveData Parse(string json, out string err)
        {
            err = null;
            try
            {
                var root = O(Json.Parse(json));
                if (root == null) { err = "not_an_object"; return null; }
                int fmt = (int)D(root, "format", -1);
                if (fmt != 1 && fmt != 2) { err = "unsupported_format"; return null; }
                var d = new AswSaveData { Format = fmt, MissionTimeS = D(root, "mission_time_s"), SavePath = S(root, "save_path") };
                { object sv; d.Sav = root.TryGetValue("sav", out sv) ? SavStamp.FromJson(sv) : null; }
                foreach (object go in L(root["groups"]) ?? new List<object>())
                {
                    var g = O(go);
                    var dto = new AswGroupDto { Name = S(g, "name"), Order = S(g, "order") ?? "none", TargetName = S(g, "target_name"),
                                                TargetUid = (int)D(g, "target_uid"), TargetKind = S(g, "target_kind") == "formation" ? "formation" : "ship", Loadouts = LoadoutsFrom(g), Seq = (int)D(g, "seq"), AutoName = g.ContainsKey("auto_name") && g["auto_name"] is bool ? (bool?)(bool)g["auto_name"] : null, Depth = S(g, "depth"), Params = ParamsFrom(O(g.ContainsKey("params") ? g["params"] : null)) };
                    if (g.ContainsKey("strike") && g["strike"] is Dictionary<string, object>)
                    {
                        var so = O(g["strike"]);
                        dto.RefUid = (int)D(so, "ref_uid"); dto.RadiusNm = D(so, "radius_nm"); dto.Targets = S(so, "targets"); dto.Airborne = (int)D(so, "airborne");
                        foreach (object k in L(so.ContainsKey("skip") ? so["skip"] : null) ?? new List<object>()) if (k is double) dto.Skip.Add((int)(double)k);
                    }
                    foreach (object t in L(g["tactics"]) ?? new List<object>()) dto.Tactics.Add((string)t);
                    if (g.ContainsKey("course")) dto.Course = CourseFrom(O(g["course"]));
                    if (g.ContainsKey("barrier")) dto.Barrier = BandFrom(O(g["barrier"]));
                    if (g.ContainsKey("frame_axis_deg") && g["frame_axis_deg"] is double) dto.FrameAxisDeg = (double)g["frame_axis_deg"];   // T-238
                    dto.AreaPattern = S(g, "area_pattern");
                    var rect = g.ContainsKey("rect") ? L(g["rect"]) : null;
                    if (rect != null) { dto.Rect = new double[rect.Count]; for (int i = 0; i < rect.Count; i++) dto.Rect[i] = (double)rect[i]; }
                    foreach (object mo in L(g["members"]) ?? new List<object>())
                    { var m = O(mo); dto.Members.Add(new AswMemberRef { Uid = (int)D(m, "uid"), Name = S(m, "name"), Type = S(m, "type"), Deck = m.ContainsKey("deck") ? (int)D(m, "deck") : 0 }); }
                    d.Groups.Add(dto);
                }
                foreach (object bo in L(root["buoys"]) ?? new List<object>())
                { var b = O(bo); d.Buoys.Add(new AswBuoyDto { Lat = D(b, "lat"), Lon = D(b, "lon"), DroppedS = D(b, "dropped_s"), LifeS = D(b, "life_s"), Ammo = S(b, "ammo"), Group = S(b, "group") }); }
                foreach (object co in L(root["contacts"]) ?? new List<object>())
                { var c = O(co); d.Contacts.Add(new AswContactDto { Id = (int)D(c, "id"), Name = S(c, "name"), Group = S(c, "group"), Lat = D(c, "lat"), Lon = D(c, "lon"), SeenS = D(c, "seen_s") }); }
                foreach (object mo in L(root["memory"]) ?? new List<object>())
                { var m = O(mo); d.Memory.Add(new AswMemDto { Uid = (int)D(m, "uid"), Name = S(m, "name"), Type = S(m, "type"), Roe = S(m, "roe"), Depth = S(m, "depth"), Radar = AirRadarRules.FromJson(m.ContainsKey("radar") ? O(m["radar"]) : null) }); }
                object sec;
                d.Sar = SarSave.Parse(root.TryGetValue("sar", out sec) ? O(sec) : null);   // absent (format 1) -> empty, never throws
                return d;
            }
            catch (Exception e)
            {
                err = "parse: " + e.Message;
                return null;
            }
        }

        // ---- sample and self test ----------------------------------------------------------------------------------

        private static AswSaveData SampleData()
        {
            var d = new AswSaveData { MissionTimeS = 1234.5, SavePath = "saves/Yorktown_01.sav" };
            var g = new AswGroupDto { Name = "ASW-1", Order = "course", TargetName = "Yorktown", TargetUid = 17, TargetKind = "formation", Seq = 3, AutoName = true, Depth = "alternate",
                                      Rect = new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 } };
            g.Tactics.Add("corridor"); g.Tactics.Add("dip");
            g.Course = new AswCourseSpec { CorridorXNm = 30, CorridorLNm = 40, DipLNm = 20, Depth = "auto", BarrierRows = 3, BarrierGapNm = 8 }; g.Course.Tactics.Add("dip"); g.Course.Tactics.Add("corridor");
            g.Loadouts["SH-3H"] = "ASWKiller"; g.Loadouts["SH-60B"] = "auto";
            g.Members.Add(new AswMemberRef { Uid = 101, Name = "Helo 1", Type = "SH-3H" });
            g.Members.Add(new AswMemberRef { Uid = 102, Name = "Helo 2", Type = "SH-3H" });
            g.Barrier = new AswBarrierBandDto { Lat = 35.0, Lon = 20.0, AxisDeg = 90, T0 = 1100, NextSeq = 2 };
            g.Barrier.Rows.Add(new AswBarrierRowDto { Seq = 0, Lat = 35.0, Lon = 20.1, AxisDeg = 90, HalfNm = 17.84, XNm = 8, LaidS = 1100, Buoys = 10, Passed = true });
            g.Barrier.Rows.Add(new AswBarrierRowDto { Seq = 1, Lat = 35.0, Lon = 20.2, AxisDeg = 90, HalfNm = 15.86, XNm = 16, LaidS = 1100, Buoys = 9, Open = { new[] { 35.01, 20.2 }, new[] { 35.02, 20.2 } } });
            g.FrameAxisDeg = 167;   // T-238
            d.Groups.Add(g);
            d.Buoys.Add(new AswBuoyDto { Lat = 35.01, Lon = 20.02, DroppedS = 1000, LifeS = 28800, Ammo = "usn_ssq-53", Group = "ASW-1" });
            d.Contacts.Add(new AswContactDto { Id = 3, Name = "Sub 3", Group = "ASW-1", Lat = 35.05, Lon = 20.1, SeenS = 1200 });
            d.Memory.Add(new AswMemDto { Uid = 101, Name = "Helo 1", Type = "SH-3H", Roe = "weapons_free", Depth = "Intermittent" });
            d.Groups.Add(new AswGroupDto { Name = "SAR-1", Order = "sar", Depth = "alternate" });
            d.Sar.Groups.Add(new SarGroupOrderDto { Group = "SAR-1", AreaKind = "circle", OnComplete = "loiter", Lat = 35.1, Lon = 20.1, RadiusNm = 15 });
            d.Sar.Ships.Add(new SarShipDto { Uid = 17, Name = "Yorktown", Type = "CG-47", RadiusNm = 10, OnComplete = "restore" });
            d.Sar.RescuedByMod = 7;
            return d;
        }

        /// <summary>ADR-0080 Q2 (customer 2026-10-22): "all" = an old stack uid (HangarUid) takes every free machine of its type on that deck; "one" = a single machine.</summary>
        internal static string StackRestore = "all";

        private static string SlotSelfTest()
        {
            var d = SampleData();
            d.Groups[0].Members[1].Deck = 77;
            string j = ToJson(d);
            if (!j.Contains("\"deck\":77") || j.IndexOf("\"deck\"", StringComparison.Ordinal) != j.LastIndexOf("\"deck\"", StringComparison.Ordinal)) return "deck written only when set";
            string err;
            var back = Parse(j, out err);
            if (back == null || back.Groups[0].Members[1].Deck != 77 || back.Groups[0].Members[0].Deck != 0) return "deck roundtrip";
            if (ToJson(back) != j) return "slot roundtrip differs";
            var old = Parse(ToJson(SampleData()), out err);
            if (old == null || old.Groups[0].Members[1].Deck != 0) return "old file reads deck 0";
            if (j.Contains("\"sav\"") || old.Sav != null) return "sav absent without a stamp";
            d.Sav = SavStamp.Now(2425.5, new DateTime(2026, 10, 23, 2, 7, 37, DateTimeKind.Utc), "hook");
            string js = ToJson(d);
            var bs = Parse(js, out err);
            if (bs == null || bs.Sav == null || Math.Abs(bs.Sav.ElapsedS - 2425.5) > 1e-9 || bs.Sav.Created != "2026-10-23T02:07:37Z" || ToJson(bs) != js) return "sav roundtrip";
            return null;
        }

        /// <summary>Reference file for the pytest side (selfcheck-ui asw-save-sample).</summary>
        public static string Sample() { return ToJson(SampleData()); }

        private static string MatchTakenSelfTest()
        {
            Func<int, AswMemberRef> R = u => new AswMemberRef { Uid = u, Name = "Ka-27PL", Type = "wp_ka-27" };
            var none = new HashSet<int>(); var file = new HashSet<int> { 1, 2 };
            var two = new List<AswMemberRef> { R(11), R(12) };
            if (Match(R(1), two, none, file) != 0) return "match taken first";
            if (Match(R(2), two, new HashSet<int> { 11 }, file) != 1) return "match taken skipped";
            if (Match(R(2), two, new HashSet<int> { 11, 12 }, file) != -1) return "match all taken";
            var chg = new List<AswMemberRef> { R(2), R(7) };
            if (Match(R(1), chg, none, file) != 1) return "match name not a file uid";
            if (Match(R(2), chg, none, file) != 0) return "match file uid by uid";
            if (Match(R(1), new List<AswMemberRef> { R(1) }, new HashSet<int> { 1 }, file) != -1) return "match taken by uid";
            return null;
        }

        private static string MemoryMatchSelfTest()
        {
            Func<int, string, string, AswMemDto> M = (u, n, t) => new AswMemDto { Uid = u, Name = n, Type = t };
            Func<int, string, AswMemberRef> R = (u, n) => new AswMemberRef { Uid = u, Name = n, Type = "wp_ka-27" };
            var none = new HashSet<int>(); var both = new HashSet<int> { 476, 1388 };
            var two = new List<AswMemDto> { M(476, "Ka-27PL", "wp_ka-27"), M(1388, "Ka-27PL", "wp_ka-27") };
            if (MatchMemory(R(476, "Ka-27PL"), two, none, both) != 0 || MatchMemory(R(1388, "Ka-27PL"), two, none, both) != 1) return "memory by uid, same name";
            if (MatchMemory(R(476, "Ka-27PL"), two, new HashSet<int> { 0 }, both) != -1) return "memory taken once";
            var dup = new List<AswMemDto> { M(1388, "Ka-27PL", "wp_ka-27"), M(1388, "Ka-27PL", "wp_ka-27") };
            if (MatchMemory(R(1388, "Ka-27PL"), dup, none, both) != 0) return "memory dup first";
            if (MatchMemory(R(476, "Ka-27PL"), dup, none, both) != -1) return "memory dup not stolen";
            if (MatchMemory(R(1388, "Ka-27PL"), dup, new HashSet<int> { 0 }, both) != 1) return "memory dup second";
            var chg = new List<AswMemDto> { M(5, "Helo 3", "wp_ka-27") };
            if (MatchMemory(R(7, "Helo 3"), chg, none, new HashSet<int> { 7 }) != 0) return "memory by name";
            if (MatchMemory(R(7, "Helo 3"), chg, none, new HashSet<int> { 5, 7 }) != -1) return "memory name of a file uid";
            var old = new List<AswMemDto> { M(9, "Helo 3", "SH-3H") };
            if (MatchMemory(new AswMemberRef { Uid = 9, Name = "Helo 3", Type = "usn_sh-3h", Label = "SH-3H" }, old, none, new HashSet<int> { 9 }) != 0) return "memory by label";
            return null;
        }

        public static string SelfTest()
        {
            if (Key("/A/B.sav") != Key("/a/b.SAV")) return "key case";
            if (Key("/a") == Key("/b")) return "key distinct";
            if (Key("x").Length != 40) return "key length";
            string slotErr = SlotSelfTest();
            if (slotErr != null) return slotErr;
            var d = SampleData();
            string j1 = ToJson(d);
            string err;
            var back = Parse(j1, out err);
            if (back == null) return "parse: " + err;
            if (ToJson(back) != j1) return "roundtrip differs";
            if (back.Groups[0].Members.Count != 2 || back.Groups[0].Rect.Length != 8 || back.Groups[0].Tactics[1] != "dip") return "roundtrip content";
            if (back.Groups[0].Course == null || back.Groups[0].Course.CorridorXNm != 30 || back.Groups[0].Course.CorridorLNm != 40 || back.Groups[0].Course.Tactics.Count != 2 || back.Groups[1].Course != null) return "course roundtrip";
            // T-229: N and I of the group and the band of the sliding barrier survive; an old file reads 2 / 5 and no band
            { var bg = back.Groups[0]; var bb = bg.Barrier;
              if (bg.Course.BarrierRows != 3 || bg.Course.BarrierGapNm != 8 || bb == null || bb.NextSeq != 2 || bb.Rows.Count != 2 || !bb.Rows[0].Passed || bb.Rows[1].Passed || bb.Rows[1].Open.Count != 2 || bb.Rows[1].Buoys != 9 || Math.Abs(bb.Rows[1].Open[1][0] - 35.02) > 1e-9 || back.Groups[1].Barrier != null) return "barrier band roundtrip";
              var oldCourse = Parse(j1.Replace(",\"barrier_rows\":3,\"barrier_gap_nm\":8", ""), out err);
              if (oldCourse == null || oldCourse.Groups[0].Course.BarrierRows != 2 || oldCourse.Groups[0].Course.BarrierGapNm != 5) return "barrier rows / gap absent -> 2 / 5";
              int bi = j1.IndexOf(",\"barrier\":{", StringComparison.Ordinal), ri = j1.IndexOf(",\"rect\"", bi, StringComparison.Ordinal);
              var noBand = Parse(j1.Substring(0, bi) + j1.Substring(ri), out err);
              if (noBand == null || noBand.Groups[0].Barrier != null) return "an old file has no band " + err; }
            // T-238: the axis of the anchor survives; an old file has none
            { if (back.Groups[0].FrameAxisDeg != 167 || back.Groups[1].FrameAxisDeg.HasValue) return "frame axis roundtrip";
              var noAxis = Parse(j1.Replace(",\"frame_axis_deg\":167", ""), out err);
              if (noAxis == null || noAxis.Groups[0].FrameAxisDeg.HasValue || j1.IndexOf("frame_axis_deg", StringComparison.Ordinal) < 0) return "frame axis absent -> null " + err; }
            if (!Fresh(d, 1238.0) || !Fresh(d, 1230.0) || Fresh(d, 1240.0) || Fresh(d, 1000.0)) return "fresh";
            var live = new List<AswMemberRef> { new AswMemberRef { Uid = 7, Name = "Helo 2", Type = "SH-3H" }, new AswMemberRef { Uid = 101, Name = "X", Type = "SH-3H" } };
            if (Match(new AswMemberRef { Uid = 101, Name = "Helo 1", Type = "SH-3H" }, live) != 1) return "match uid";
            if (Match(new AswMemberRef { Uid = 102, Name = "Helo 2", Type = "SH-3H" }, live) != 0) return "match name";
            if (Match(new AswMemberRef { Uid = 5, Name = "Nobody", Type = "SH-3H" }, live) != -1) return "match none";
            { string m5 = MatchTakenSelfTest(); if (m5 != null) return m5; }
            { string me = MemoryMatchSelfTest(); if (me != null) return me; }
            var lab = new List<AswMemberRef> { new AswMemberRef { Uid = 9, Name = "Helo 3", Type = "usn_sh-3h", Label = "SH-3H" } };
            if (Match(new AswMemberRef { Uid = 9, Name = "Helo 3", Type = "SH-3H" }, lab) != 0) return "match old class name by uid";
            if (Match(new AswMemberRef { Uid = 4, Name = "Helo 3", Type = "SH-3H" }, lab) != 0) return "match old class name by name";
            if (back.Format != 2 || back.Sar.Groups.Count != 1 || back.Sar.Groups[0].AreaKind != "circle" || back.Sar.Ships.Count != 1 || back.Sar.RescuedByMod != 7) return "sar section";
            if (back.Groups[1].Order != "sar") return "sar group order";
            if (back.Groups[0].Seq != 3 || back.Groups[1].Seq != 0) return "seq roundtrip";
            if (back.Groups[0].AutoName != true || back.Groups[1].AutoName.HasValue) return "auto_name roundtrip";
            // T-142 K: target kind and the chosen loadouts survive; "auto" is not saved; an old file reads as ship / nothing chosen
            if (back.Groups[0].TargetKind != "formation" || back.Groups[1].TargetKind != "ship" || back.Groups[0].Loadouts.Count != 1 || back.Groups[0].Loadouts["SH-3H"] != "ASWKiller") return "target kind / loadouts roundtrip";
            var oldK = Parse(j1.Replace(",\"target_kind\":\"formation\"", "").Replace(",\"loadouts\":{\"SH-3H\":\"ASWKiller\"}", ""), out err);
            if (oldK == null || oldK.Groups[0].TargetKind != "ship" || oldK.Groups[0].Loadouts.Count != 0) return "target kind / loadouts absent";
            // an old file has no "seq": reads as 0
            var noSeq = Parse(j1.Replace(",\"seq\":3", ""), out err);
            if (noSeq == null || noSeq.Groups[0].Seq != 0) return "seq absent -> 0";
            // a format 1 file (before T-109) has no "sar" section and still reads
            string old = j1.Replace("\"format\":2", "\"format\":1");
            int cut = old.IndexOf(",\"sar\":", StringComparison.Ordinal);
            if (cut > 0) old = old.Substring(0, cut) + "}";
            var oldBack = Parse(old, out err);
            if (oldBack == null || oldBack.Format != 1 || oldBack.Groups.Count != 2) return "format 1 read: " + err;
            if (oldBack.Sar == null || oldBack.Sar.Groups.Count != 0 || oldBack.Sar.Ships.Count != 0) return "format 1 has no sar";
            Parse(j1.Replace("\"format\":2", "\"format\":9"), out err);
            if (err != "unsupported_format") return "format check";
            // T-173: a strike group keeps its order and the "do not attack" list; the file of an older mod has no such section
            var sd = SampleData();
            sd.Groups.Add(new AswGroupDto { Name = "УДАР-2", Order = "strike", RefUid = 17, RadiusNm = 0, Targets = "hostile", Airborne = 1, Skip = new List<int> { 41, 42 } });
            var sj = ToJson(sd);
            var sb2 = Parse(sj, out err);
            if (sb2 == null || sb2.Groups[2].Order != "strike" || sb2.Groups[2].RefUid != 17 || sb2.Groups[2].Targets != "hostile" || sb2.Groups[2].Airborne != 1 || sb2.Groups[2].Skip.Count != 2 || sb2.Groups[2].Skip[1] != 42) return "strike roundtrip";
            if (ToJson(sb2) != sj) return "strike roundtrip differs";
            Parse("{bad", out err);
            if (err == null) return "bad json";
            if (Parse(Sample(), out err) == null) return "sample parse";
            return null;
        }
    }
}
