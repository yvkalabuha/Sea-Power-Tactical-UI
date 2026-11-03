// T-148 A2 (reference draft, written while validating the A1 tests): the draft of an ASW order (ADR-0049 decision Q3).
// Pure model: no Unity, no game. Mode is course | area (the SAR tab uses SarPanel). Texts: UiText keys asw.btn.* / asw.hint.* / asw.fld.* (lang/*/asw_window.ini).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>
    /// The draft of an ASW order (ADR-0049 Q3, ADR-0076 s.7 / ADR-0077): no text fields. Course: the chips dip | corridor | barrier (none = "auto by the group"), X and the standoff
    /// of each tactic; area: the pattern, the bearing, the distance, the side. Every field has a value from the first opening (group -> last order of the mode -> [Asw] defaults).
    /// </summary>
    internal sealed class AswOrderDraft
    {
        public string Mode = "course";                 // course|area
        public readonly bool[] On = new bool[3];       // course chips: dip | corridor | barrier; none lit = auto
        public AswCourseSpec Course = new AswCourseSpec();   // X and the standoffs (Tactics of it are not used: On is)
        public string AreaPattern = "auto";            // auto | grid | dip | mad
        public string Depth = "auto";                  // auto | above | below | alternate
        public double BearingDeg, DistNm = 20, SizeNm = 10;   // area by bearing: not remembered (customer: the target, the rectangle, the bearing and the distance are of this order)
        public bool GeoDirty;                          // X, standoff, area, target edited after DraftFrom
        public string AreaSource;                      // null|rect|bearing
        public double[] Rect;                          // rect: 8 numbers lat/lon of the corners
        public int TargetUid; public string TargetKind; // 0 = the ship selected in the game (the default is filled by the window)
        public Dictionary<string, string> Loadouts = new Dictionary<string, string>();   // filled by T-142 K5
        public string ForGroup, ForOrder;
        public bool Reload;                            // set by the window after a successful order: the next DraftFrom reloads the draft from the group

        public static readonly string[] CourseIds = { "dip", "corridor", "barrier" };

        /// <summary>The tactic ids the chips stand for, in the order dip > corridor > barrier; empty = auto.</summary>
        public List<string> Chosen()
        {
            var l = new List<string>();
            for (int i = 0; i < CourseIds.Length; i++) if (On[i]) l.Add(CourseIds[i]);
            return l;
        }

        /// <summary>"dip+corridor" form of the chips ("auto" = none).</summary>
        public string Combo { get { return AswPatternMath.ComboOf(Chosen()); } }

        public void SetCombo(string combo)
        {
            var l = AswPatternMath.ComboTactics(combo);
            for (int i = 0; i < CourseIds.Length; i++) On[i] = l.Contains(CourseIds[i]);
        }

        /// <summary>T-334: after a chip press: corridor and barrier both on -> the one that was on before goes off (the pressed one wins).</summary>
        public void ApplyExclusive(bool[] before)
        {
            if (!(On[1] && On[2])) return;
            if (before != null && before.Length > 1 && before[1] && !before[2]) On[1] = false;
            else if (before != null && before.Length > 2 && before[2] && !before[1]) On[2] = false;
            else On[1] = false;   // both were on (a draft that was not normalised): the barrier wins
        }

        /// <summary>What a mode draws as "the tactic" of the draft: course = the combo, area = the pattern.</summary>
        public string TacticKey { get { return Mode == "area" ? AreaPattern : Combo; } }

        /// <summary>The tactics of the course order the controller would run: the chips, or the default of an empty selection (dip + corridor).</summary>
        public List<string> Effective()
        {
            var l = Chosen();
            if (l.Count == 0) { l.Add("dip"); l.Add("corridor"); }
            return l;
        }
    }

    internal static class AswOrderPanel
    {
        private static string T(string key, params object[] args) { return UiText.Fmt(key, args); }

        private static string DepthOf(AswGroupView g) { return g == null || string.IsNullOrEmpty(g.Depth) ? "auto" : g.Depth; }

        private static string GroupKey(string mode, AswGroupView g)
        {
            if (g == null) return null;
            return mode == "area" ? (string.IsNullOrEmpty(g.AreaPattern) ? "auto" : g.AreaPattern) : AswPatternMath.ComboOf(g.Course.Tactics);
        }

        private static string N(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        private static double ParseNum(string s, double dflt)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : dflt;
        }

        // ---- the memory of the last order of a mode ([Ui] WindowState, val=asw.last.course|area:...) ---------------------------------------------

        public const string LastCourseId = "asw.last.course", LastAreaId = "asw.last.area";

        /// <summary>course: combo/corridorX/barrierX/dipL/corridorL/barrierL/depth/rows/gap (T-229; the old form has 7 fields); area: pattern/size/depth. Only [A-Za-z0-9+._/-] (UiWindowStateStore.SetValue).</summary>
        public static string FormatLast(AswOrderDraft d)
        {
            if (d.Mode == "area") return d.AreaPattern + "/" + N(d.SizeNm) + "/" + d.Depth;
            var c = d.Course;
            return d.Combo + "/" + N(c.CorridorXNm) + "/" + N(c.BarrierXNm) + "/" + N(c.DipLNm) + "/" + N(c.CorridorLNm) + "/" + N(c.BarrierLNm) + "/" + d.Depth + "/" + c.BarrierRows.ToString(CultureInfo.InvariantCulture) + "/" + N(c.BarrierGapNm);
        }

        /// <summary>Reads a remembered order into the draft; false = junk (the draft is left as it was).</summary>
        public static bool ParseLast(string mode, string text, AswOrderDraft d)
        {
            if (string.IsNullOrEmpty(text) || d == null) return false;
            string[] p = text.Split('/');
            if (mode == "area")
            {
                if (p.Length != 3 || Array.IndexOf(AswPatternMath.AreaPatterns, p[0]) < 0 || !IsDepth(p[2])) return false;
                d.AreaPattern = p[0]; d.SizeNm = AswPatternMath.Snap(ParseNum(p[1], d.SizeNm), AswPatternMath.AreaSizeList); d.Depth = p[2];
                return true;
            }
            if ((p.Length != 7 && p.Length != 9) || Array.IndexOf(AswPatternMath.CourseCombos, AswPatternMath.NormCombo(p[0])) < 0 || !IsDepth(p[6])) return false;
            d.SetCombo(p[0]);
            d.Course.CorridorXNm = AswPatternMath.Snap(ParseNum(p[1], d.Course.CorridorXNm), AswPatternMath.WidthList);
            d.Course.BarrierXNm = AswPatternMath.Snap(ParseNum(p[2], d.Course.BarrierXNm), AswPatternMath.WidthList);
            d.Course.DipLNm = AswPatternMath.Snap(ParseNum(p[3], d.Course.DipLNm), AswPatternMath.StandoffList);
            d.Course.CorridorLNm = AswPatternMath.Snap(ParseNum(p[4], d.Course.CorridorLNm), AswPatternMath.StandoffList);
            d.Course.BarrierLNm = AswPatternMath.Snap(ParseNum(p[5], d.Course.BarrierLNm), AswPatternMath.StandoffList);
            d.Depth = p[6];
            if (p.Length == 9)   // T-229: the rows and the interval of the sliding barrier; the old 7-field line keeps the defaults of [Asw]
            {
                d.Course.BarrierRows = Math.Max(1, Math.Min(5, (int)Math.Round(ParseNum(p[7], d.Course.BarrierRows))));
                d.Course.BarrierGapNm = AswPatternMath.Snap(ParseNum(p[8], d.Course.BarrierGapNm), AswPatternMath.BarrierGapList);
            }
            return true;
        }

        private static bool IsDepth(string s) { return s == "auto" || s == "above" || s == "below" || s == "alternate"; }

        /// <summary>The target a new order starts with: the target of the group's own course order, else the ship selected in the game, else the home ship of the first aircraft. null = none known.</summary>
        public static AswTargetOption DefaultTarget(AswGroupView g, int selectedUid)
        {
            if (g == null || g.CourseTargets == null) return null;
            if (g.Order == "course" && g.TargetUid != 0) foreach (var o in g.CourseTargets) if (o.Uid == g.TargetUid && (o.Kind ?? "ship") == (g.TargetKind ?? "ship")) return o;
            if (selectedUid != 0) foreach (var o in g.CourseTargets) if (o.Uid == selectedUid) return o;
            foreach (var o in g.CourseTargets) if (o.Selected) return o;
            foreach (var r in g.MemberRows)
                if (r.ShipUid != 0) foreach (var o in g.CourseTargets) if (o.Uid == r.ShipUid) return o;
            return null;
        }

        public static AswOrderDraft DraftFrom(AswGroupView g, string mode, AswOrderDraft keep, AswPatternParams def = null, IUiWindowValues mem = null, double areaSizeNm = 10, int selectedUid = 0)
        {
            if (g != null && keep != null && !keep.Reload && keep.ForGroup == g.Name && keep.Mode == mode && keep.ForOrder == g.Order) return keep;
            if (g == null && keep != null && keep.Mode == mode && keep.ForGroup == null) return keep;
            def = def ?? new AswPatternParams();
            var d = new AswOrderDraft { Mode = mode, ForGroup = g == null ? null : g.Name, ForOrder = g == null ? null : g.Order };
            // 1. the defaults of [Asw]
            d.SetCombo(def.CourseTactic);
            d.Course.CorridorXNm = AswPatternMath.Snap(def.CorridorWidthNm, AswPatternMath.WidthList);
            d.Course.BarrierXNm = AswPatternMath.Snap(def.BarrierWidthNm, AswPatternMath.WidthList);
            d.Course.DipLNm = AswPatternMath.Snap(def.DipStandoffNm, AswPatternMath.StandoffList);
            d.Course.CorridorLNm = AswPatternMath.Snap(def.CorridorStandoffNm, AswPatternMath.StandoffList);
            d.Course.BarrierLNm = AswPatternMath.Snap(def.BarrierStandoffNm, AswPatternMath.StandoffList);
            d.Course.BarrierRows = Math.Max(1, Math.Min(5, def.BarrierRows));
            d.Course.BarrierGapNm = AswPatternMath.Snap(def.BarrierGapNm, AswPatternMath.BarrierGapList);
            d.AreaPattern = Array.IndexOf(AswPatternMath.AreaPatterns, def.AreaTactic) >= 0 ? def.AreaTactic : "auto";
            d.SizeNm = AswPatternMath.Snap(areaSizeNm, AswPatternMath.AreaSizeList);
            d.DistNm = 20;
            d.Depth = IsDepth(def.BuoyDepth) ? def.BuoyDepth : "auto";
            // 2. the last order of this mode
            if (mem != null) ParseLast(mode, mem.Value(mode == "area" ? LastAreaId : LastCourseId, ""), d);
            // 3. the group's own order of this mode
            if (g != null && g.Order == mode)
            {
                if (mode == "course")
                {
                    d.SetCombo(AswPatternMath.ComboOf(g.Course.Tactics));
                    d.Course = new AswCourseSpec { CorridorXNm = g.Course.CorridorXNm, BarrierXNm = g.Course.BarrierXNm, DipLNm = g.Course.DipLNm, CorridorLNm = g.Course.CorridorLNm, BarrierLNm = g.Course.BarrierLNm, BarrierRows = g.Course.BarrierRows, BarrierGapNm = g.Course.BarrierGapNm };
                }
                else d.AreaPattern = string.IsNullOrEmpty(g.AreaPattern) ? "auto" : g.AreaPattern;
                d.Depth = DepthOf(g);
            }
            else if (g != null && g.Order == "none" && g.Depth != null && IsDepth(g.Depth) && mem == null) d.Depth = g.Depth;
            // a chip that the composition does not offer is not lit
            if (g != null && g.Order != mode) DropGreyChips(g, d);
            if (keep != null)
            {
                d.BearingDeg = keep.BearingDeg; d.DistNm = keep.DistNm;   // not remembered between orders, but they outlive a reload of the same draft
                if (keep.ForGroup == d.ForGroup) { d.TargetUid = keep.TargetUid; d.TargetKind = keep.TargetKind; }
            }
            if (mode == "course" && d.TargetUid == 0)
            {
                var t = DefaultTarget(g, selectedUid);
                if (t != null) { d.TargetUid = t.Uid; d.TargetKind = t.Kind ?? "ship"; }
            }
            return d;
        }

        private static void DropGreyChips(AswGroupView g, AswOrderDraft d)
        {
            if (d.Mode == "area") { foreach (var o in AswWindowModel.TacticsFor("area", g)) if (o.Tactic == d.AreaPattern && !o.Enabled) d.AreaPattern = "auto"; return; }
            foreach (var o in AswWindowModel.TacticsFor("course", g))
            {
                int i = Array.IndexOf(AswOrderDraft.CourseIds, o.Tactic);
                if (i >= 0 && !o.Enabled) d.On[i] = false;
            }
        }

        internal static bool NeedAreaFor(AswGroupView g, AswOrderDraft d) { return NeedArea(g, d); }

        private static bool NeedArea(AswGroupView g, AswOrderDraft d)
        {
            return d.Mode == "area" && d.AreaSource == null && !(g != null && g.Order == "area" && g.Rect != null && g.Rect.Length >= 8);
        }

        /// <summary>The chip / pattern the draft names that the composition does not allow (the order is refused with its reason); null = fine.</summary>
        private static AswWindowModel.TacticOption BadOption(AswGroupView g, AswOrderDraft d)
        {
            foreach (var o in AswWindowModel.TacticsFor(d.Mode, g))
            {
                if (o.Enabled) continue;
                if (d.Mode == "area" ? o.Tactic == d.AreaPattern : d.Chosen().Contains(o.Tactic)) return o;
            }
            return null;
        }

        public static bool SameAsOrder(AswGroupView g, AswOrderDraft d)
        {
            if (g == null || d == null) return false;
            return g.Order == d.Mode && !d.GeoDirty && d.AreaSource == null && d.TacticKey == GroupKey(d.Mode, g) && d.Depth == DepthOf(g);
        }

        public static string DirtyFields(AswGroupView g, AswOrderDraft d)
        {
            if (g == null || d == null) return "";
            var l = new List<string>();
            if (d.TacticKey != GroupKey(d.Mode, g)) l.Add(T("asw.fld.tactic"));
            if (d.Depth != DepthOf(g)) l.Add(T("asw.fld.depth"));
            if (d.AreaSource != null) l.Add(T("asw.fld.area"));
            else if (d.GeoDirty) l.Add(T(d.Mode == "area" ? "asw.fld.area" : "asw.fld.course"));
            return string.Join(", ", l);
        }

        /// <summary>replace | need_area | dirty | null.</summary>
        public static string Hint(AswGroupView g, AswOrderDraft d)
        {
            if (g != null && g.Order != "none" && g.Order != d.Mode) return "replace";
            if (NeedArea(g, d)) return "need_area";
            if (g != null && g.Order == d.Mode && !SameAsOrder(g, d)) return "dirty";
            return null;
        }

        public static UiButtonState Action(AswGroupView g, AswOrderDraft d)
        {
            bool apply = g != null && g.Order == d.Mode;
            var s = new UiButtonState { Id = apply ? "apply" : d.Mode == "area" ? "start_area" : "start_course" };
            s.Label = apply ? T("asw.btn.apply") : d.Mode == "area" ? T("asw.btn.start_area") : T("asw.btn.start_course");
            var opt = BadOption(g, d);
            if (opt != null) { s.Tip = opt.DisabledReason; return s; }
            if (NeedArea(g, d)) { s.Tip = T("asw.hint.need_area"); return s; }
            if (apply && SameAsOrder(g, d)) { s.Tip = T("asw.hint.same"); return s; }
            s.Enabled = true;
            if (apply) s.Tip = T("asw.hint.dirty", DirtyFields(g, d));
            else if (g != null && g.Order != "none") s.Tip = T("asw.hint.replace");
            return s;
        }

        private static List<object> TacticList(AswOrderDraft d)
        {
            var l = new List<object>();
            if (d.Mode == "area") { if (d.AreaPattern != "auto") l.Add(d.AreaPattern); }
            else foreach (string t in d.Chosen()) l.Add(t);
            return l;
        }

        private static Dictionary<string, object> StandoffArg(AswOrderDraft d)
        {
            return new Dictionary<string, object> { { "dip", d.Course.DipLNm }, { "corridor", d.Course.CorridorLNm }, { "barrier", d.Course.BarrierLNm } };
        }

        private static void CourseArgs(AswOrderDraft d, Dictionary<string, object> a)
        {
            a["corridor_x_nm"] = d.Course.CorridorXNm; a["barrier_x_nm"] = d.Course.BarrierXNm; a["standoff"] = StandoffArg(d);
            a["barrier_rows"] = (double)d.Course.BarrierRows; a["barrier_gap_nm"] = d.Course.BarrierGapNm;   // T-229
        }

        private static void AreaArgs(AswOrderDraft d, Dictionary<string, object> a)
        {
            a["pattern"] = d.AreaPattern;
            if (d.AreaSource == "rect")
            {
                var pairs = new List<object>();
                for (int i = 0; i < 8; i += 2) pairs.Add(new List<object> { d.Rect[i], d.Rect[i + 1] });
                a["rect"] = pairs;
            }
            else { a["bearing"] = d.BearingDeg; a["dist_nm"] = d.DistNm; a["size_nm"] = d.SizeNm; }
        }

        /// <summary>The arguments of a full by-course order of the draft (group, tactics, X, standoffs, depth); used when only the target changed.</summary>
        public static Dictionary<string, object> CourseOrderArgs(AswGroupView g, AswOrderDraft d)
        {
            var a = new Dictionary<string, object> { { "tactics", TacticList(d) }, { "depth", d.Depth } };
            if (g != null) a["group"] = g.Name;
            CourseArgs(d, a);
            return a;
        }

        /// <summary>T-178 A4: the arguments of a full area order of the draft (group, depth, pattern, rect | bearing + distance + size); the "Undo" of "Stop all" replays it.</summary>
        public static Dictionary<string, object> AreaOrderArgs(AswGroupView g, AswOrderDraft d)
        {
            var a = new Dictionary<string, object> { { "depth", d.Depth } };
            if (g != null) a["group"] = g.Name;
            AreaArgs(d, a);
            return a;
        }

        /// <summary>The commands of "Start" / "Apply". A new order, another order or a geometry edit sends the full order; the same order sends only the difference
        /// (tactics -> asw_set_tactics, depth -> asw_depth) so the plan is not rebuilt. err: need_area | tactic_unavailable.</summary>
        public static List<UiCmd> Commands(AswGroupView g, AswOrderDraft d, out string err)
        {
            err = null;
            var cmds = new List<UiCmd>();
            if (BadOption(g, d) != null) { err = "tactic_unavailable"; return cmds; }
            if (NeedArea(g, d)) { err = "need_area"; return cmds; }
            if (d.AreaSource == "rect" && d.Mode == "area" && (d.Rect == null || d.Rect.Length < 8)) { err = "need_area"; return cmds; }
            bool full = g == null || g.Order != d.Mode || d.GeoDirty || (d.Mode == "area" && d.AreaSource != null);
            if (full)
            {
                var a = new Dictionary<string, object> { { "tactics", TacticList(d) }, { "depth", d.Depth } };
                if (g != null) a["group"] = g.Name;
                if (d.Mode == "course")
                {
                    CourseArgs(d, a);
                    cmds.Add(new UiCmd { Name = "asw_search_course", Args = a });
                    return cmds;
                }
                a.Remove("tactics");   // the area pattern travels as "pattern"
                AreaArgs(d, a);
                cmds.Add(new UiCmd { Name = "asw_search_area", Args = a });
                return cmds;
            }
            if (d.TacticKey != GroupKey(d.Mode, g))
                cmds.Add(new UiCmd { Name = "asw_set_tactics", Args = new Dictionary<string, object> { { "group", g.Name }, { "tactics", d.Mode == "area" ? (d.AreaPattern == "auto" ? new List<object> { "grid", "dip", "mad" } : TacticList(d)) : (d.Chosen().Count == 0 ? new List<object> { "dip", "corridor" } : TacticList(d)) } } });
            if (d.Depth != DepthOf(g))
                cmds.Add(new UiCmd { Name = "asw_depth", Args = new Dictionary<string, object> { { "group", g.Name }, { "mode", d.Depth } } });
            return cmds;
        }

        /// <summary>asw_preview: the draft built like the order itself (course: tactics, X, standoffs; area: pattern, rect | bearing + distance + size), always "keep" - the window holds the
        /// preview while its route flag is on. Null without an area of the draft.</summary>
        public static Dictionary<string, object> PreviewCommand(AswGroupView g, AswOrderDraft d)
        {
            if (d == null) return null;
            var a = new Dictionary<string, object> { { "depth", d.Depth }, { "keep", true } };
            if (g != null) a["group"] = g.Name;
            if (d.Mode == "area")
            {
                a["area"] = true;
                if (d.AreaSource == "rect") { if (d.Rect == null || d.Rect.Length < 8) return null; }
                else if (d.AreaSource != "bearing") return null;
                AreaArgs(d, a);
                return a;
            }
            if (d.Mode != "course") return null;
            a["tactics"] = TacticList(d);
            CourseArgs(d, a);
            return a;
        }

        /// <summary>"Stop ASW-1": grey without a group and without a course / area order (a SAR group has its own button).</summary>
        public static UiButtonState Stop(AswGroupView g)
        {
            var s = new UiButtonState { Id = "stop" };
            if (g == null) { s.Label = T("asw.btn.stop"); s.Tip = T("asw.hint.no_group"); return s; }
            s.Label = T("asw.btn.stop_group", g.Name);
            s.Enabled = g.Order == "course" || g.Order == "area" || g.Order == "strike";
            s.Tip = s.Enabled ? null : g.Order == "sar" ? T("asw.hint.sar_stop") : T("asw.hint.no_order");
            return s;
        }

        public static string SelfTest() { return null; }
    }
}
