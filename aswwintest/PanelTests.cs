// T-148 A1 / T-176 W1: tests of the draft model of the ASW window (AswOrderPanel): chips, X and standoff, pattern, memory of the last order, defaults.
// Texts are checked through UiText keys that A2 adds to lang/*/asw_window.ini:
//   asw.btn.start_course, asw.btn.start_area, asw.btn.apply, asw.btn.stop_group, asw.btn.stop
//   asw.hint.replace, asw.hint.need_area, asw.hint.dirty ({0} = the changed fields), asw.hint.same, asw.hint.no_group, asw.hint.no_order
//   asw.fld.tactic, asw.fld.depth, asw.fld.course, asw.fld.area
// (asw.tactic.no_mad / no_buoys / no_dip already exist.)
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class AswPanelTests
    {
        private static string T(string key) { return UiText.Get(key); }

        private static AswRow Row(bool buoys, bool dip, bool mad)
        {
            return new AswRow { Uid = 1, Name = "SH-3H", Passive = buoys ? 12 : 0, Dipping = dip, Mad = mad };
        }

        private static AswGroupView Grp(string name, string order, string depth, params string[] tactics)
        {
            var g = new AswGroupView { Name = name, Order = order, Depth = depth };
            g.Tactics.AddRange(tactics);
            if (order == "course") foreach (string t in tactics) g.Course.Tactics.Add(t);
            if (order == "area") g.AreaPattern = tactics.Length == 1 ? tactics[0] : "auto";
            g.MemberRows.Add(Row(true, true, true));
            return g;
        }

        private static string Str(Dictionary<string, object> a, string k) { object v; return a != null && a.TryGetValue(k, out v) ? v as string : null; }

        private sealed class Mem : IUiWindowValues
        {
            public readonly Dictionary<string, string> D = new Dictionary<string, string>();
            public string Value(string id, string dflt) { string v; return D.TryGetValue(id, out v) ? v : dflt; }
            public void SetValue(string id, string v) { D[id] = v; }
        }

        public static string Run()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string r = Cases();
                    if (r != null) return lang + ": " + r;
                }
            return null;
        }

        private static string Cases()
        {
            string err;
            List<UiCmd> c;

            // ---- DraftFrom: the group's own order wins, a first opening is filled from the [Asw] defaults
            var gc = Grp("ASW-1", "course", "below", "dip", "corridor");
            gc.Course.CorridorXNm = 30; gc.Course.DipLNm = 40;
            var d = AswOrderPanel.DraftFrom(gc, "course", null);
            if (d.Mode != "course" || d.Combo != "dip+corridor" || d.Depth != "below" || d.ForGroup != "ASW-1" || d.ForOrder != "course" || d.GeoDirty) return "draft from a course group";
            if (d.Course.CorridorXNm != 30 || d.Course.DipLNm != 40 || d.Course.BarrierXNm != 40) return "values of the group's order";
            d.On[2] = true;
            if (AswOrderPanel.DraftFrom(gc, "course", d) != d) return "draft of the same group and mode is kept";
            var gOther = Grp("ASW-2", "none", null);
            var d2 = AswOrderPanel.DraftFrom(gOther, "course", d);
            if (d2 == d || d2.ForGroup != "ASW-2" || d2.Combo != "auto" || d2.Depth != "auto" || d2.Course.CorridorXNm != 20 || d2.Course.BarrierXNm != 40 || d2.Course.BarrierLNm != 40 || d2.Course.DipLNm != 20) return "another group starts from the defaults: " + d2.Combo + " x" + d2.Course.CorridorXNm;
            var dArea = AswOrderPanel.DraftFrom(gc, "area", d);
            if (dArea.Mode != "area" || dArea.AreaPattern != "auto" || dArea.SizeNm != 10) return "area tab of a course group";
            if (AswOrderPanel.Hint(gc, dArea) != "replace") return "area tab of a course group says replace";
            var dn = AswOrderPanel.DraftFrom(null, "area", null);
            if (dn == null || dn.Mode != "area") return "draft without a group";
            if (AswOrderPanel.DraftFrom(null, "area", dn) != dn) return "no group keeps the draft";

            // ---- the defaults of [Asw] fill the fields (no empty field at the first opening)
            var def = new AswPatternParams { CourseTactic = "dip+corridor", AreaTactic = "mad", CorridorWidthNm = 30, BarrierWidthNm = 50, DipStandoffNm = 60, CorridorStandoffNm = 70, BarrierStandoffNm = 80, BuoyDepth = "below" };
            var dd = AswOrderPanel.DraftFrom(gOther, "course", null, def, null, 20);
            if (dd.Combo != "dip+corridor" || dd.Course.CorridorXNm != 30 || dd.Course.BarrierXNm != 50 || dd.Course.DipLNm != 60 || dd.Course.CorridorLNm != 70 || dd.Course.BarrierLNm != 80 || dd.Depth != "below") return "defaults of the course draft";
            var da = AswOrderPanel.DraftFrom(gOther, "area", null, def, null, 28);
            if (da.AreaPattern != "mad" || da.SizeNm != 30 || da.DistNm != 20) return "defaults of the area draft (28 sticks to 30): " + da.SizeNm;
            // the first action is possible at once: nothing to fill in
            var firstAct = AswOrderPanel.Action(gOther, AswOrderPanel.DraftFrom(gOther, "course", null));
            if (!firstAct.Enabled) return "start course is available at the first opening";

            // ---- the memory of the last order of a mode
            var mem = new Mem();
            d = AswOrderPanel.DraftFrom(gOther, "course", null);
            d.SetCombo("dip+barrier"); d.Course.CorridorXNm = 50; d.Course.BarrierXNm = 30; d.Course.DipLNm = 90; d.Course.CorridorLNm = 100; d.Course.BarrierLNm = 20; d.Depth = "above";
            string last = AswOrderPanel.FormatLast(d);
            if (last != "dip+barrier/50/30/90/100/20/above/2/5") return "format last: " + last;
            mem.SetValue(AswOrderPanel.LastCourseId, last);
            var back = AswOrderPanel.DraftFrom(gOther, "course", null, null, mem);
            if (back.Combo != "dip+barrier" || back.Course.CorridorXNm != 50 || back.Course.BarrierXNm != 30 || back.Course.DipLNm != 90 || back.Course.CorridorLNm != 100 || back.Course.BarrierLNm != 20 || back.Depth != "above") return "the last order comes back";
            // an order of the group itself wins over the memory
            var gOwn = Grp("ASW-8", "course", "below", "corridor");
            var own = AswOrderPanel.DraftFrom(gOwn, "course", null, null, mem);
            if (own.Combo != "corridor" || own.Depth != "below") return "the group's order wins over the memory";
            // a restart: the memory goes through the text of the window state
            var st1 = new UiWindowStateStore("", null); st1.SetValue(AswOrderPanel.LastCourseId, last);
            var st2 = UiWindowStateStore.Parse(st1.Format());
            if (st2.Value(AswOrderPanel.LastCourseId, "") != last) return "memory does not survive the restart";
            // junk in the memory is ignored
            mem.SetValue(AswOrderPanel.LastCourseId, "zig/zag");
            if (AswOrderPanel.DraftFrom(gOther, "course", null, null, mem).Combo != "auto") return "junk memory";
            var ad = AswOrderPanel.DraftFrom(gOther, "area", null); ad.AreaPattern = "grid"; ad.SizeNm = 40; ad.Depth = "alternate";
            if (AswOrderPanel.FormatLast(ad) != "grid/40/alternate") return "format last area";
            mem.SetValue(AswOrderPanel.LastAreaId, "grid/40/alternate");
            var ab = AswOrderPanel.DraftFrom(gOther, "area", null, null, mem);
            if (ab.AreaPattern != "grid" || ab.SizeNm != 40 || ab.Depth != "alternate") return "last area order";

            // ---- the default target: the group's own, the selected ship, the home ship of the first aircraft
            var gt = Grp("ASW-T", "none", null);
            gt.CourseTargets.Add(new AswTargetOption { Uid = 10, Name = "A" }); gt.CourseTargets.Add(new AswTargetOption { Uid = 11, Name = "B" }); gt.CourseTargets.Add(new AswTargetOption { Uid = 700, Name = "Home" });
            gt.MemberRows[0].ShipUid = 700;
            var tg = AswOrderPanel.DefaultTarget(gt, 11);
            if (tg == null || tg.Uid != 11) return "selected ship is the default target";
            tg = AswOrderPanel.DefaultTarget(gt, 0);
            if (tg == null || tg.Uid != 700) return "home ship is the default target";
            gt.Order = "course"; gt.TargetUid = 10;
            tg = AswOrderPanel.DefaultTarget(gt, 11);
            if (tg == null || tg.Uid != 10) return "the group's own target wins";
            if (AswOrderPanel.DefaultTarget(null, 0) != null) return "no group, no target";
            if (AswOrderPanel.DraftFrom(Grp("ASW-T2", "none", null), "course", null, null, null, 10, 0).TargetUid != 0) return "no targets, no default";
            var gt2 = Grp("ASW-T3", "none", null); gt2.CourseTargets.Add(new AswTargetOption { Uid = 12, Name = "C" });
            if (AswOrderPanel.DraftFrom(gt2, "course", null, null, null, 10, 12).TargetUid != 12) return "the draft starts with the selected ship";

            // ---- SameAsOrder
            d = AswOrderPanel.DraftFrom(gc, "course", null);
            if (!AswOrderPanel.SameAsOrder(gc, d)) return "fresh draft equals the order";
            d.On[2] = true; if (AswOrderPanel.SameAsOrder(gc, d)) return "tactics differ"; d.On[2] = false;
            d.Depth = "above"; if (AswOrderPanel.SameAsOrder(gc, d)) return "depth differs"; d.Depth = "below";
            d.GeoDirty = true; if (AswOrderPanel.SameAsOrder(gc, d)) return "geometry edit differs"; d.GeoDirty = false;
            if (AswOrderPanel.SameAsOrder(gc, AswOrderPanel.DraftFrom(gc, "area", null))) return "another mode differs";
            if (AswOrderPanel.SameAsOrder(gOther, AswOrderPanel.DraftFrom(gOther, "course", null))) return "a group without an order never equals a draft";

            // ---- DirtyFields
            d.On[0] = false; d.Depth = "above";
            string dirty = AswOrderPanel.DirtyFields(gc, d);
            if (!dirty.Contains(T("asw.fld.tactic")) || !dirty.Contains(T("asw.fld.depth"))) return "dirty fields: " + dirty;
            d = AswOrderPanel.DraftFrom(gc, "course", null); d.GeoDirty = true;
            if (!AswOrderPanel.DirtyFields(gc, d).Contains(T("asw.fld.course"))) return "dirty course geometry";

            // ---- Commands: a group without an order -> the full order with every value of the draft
            var gNone = Grp("ASW-9", "none", null);
            d = AswOrderPanel.DraftFrom(gNone, "course", null);
            c = AswOrderPanel.Commands(gNone, d, out err);
            if (err != null || c.Count != 1 || c[0].Name != "asw_search_course" || Str(c[0].Args, "group") != "ASW-9") return "start course: " + err;
            var pc = AswCommandsParse.ParseCourse(c[0].Args);
            if (pc.Err != null || pc.Tactics.Count != 0 || pc.Depth != "auto" || pc.CorridorX != 20 || pc.BarrierX != 40 || pc.StandDip != 20 || pc.StandCorridor != 20 || pc.StandBarrier != 40 || pc.LeadNm.HasValue || pc.WidthNm.HasValue) return "start course parse (auto, defaults)";
            c = AswOrderPanel.Commands(null, AswOrderPanel.DraftFrom(null, "course", null), out err);
            if (err != null || c.Count != 1 || c[0].Name != "asw_search_course" || c[0].Args.ContainsKey("group")) return "start course without a group";
            d = AswOrderPanel.DraftFrom(gNone, "course", null); d.SetCombo("barrier+dip"); d.Course.BarrierXNm = 50; d.Course.BarrierLNm = 90;
            c = AswOrderPanel.Commands(gNone, d, out err);
            pc = AswCommandsParse.ParseCourse(c[0].Args);
            if (pc.Err != null || pc.Tactics.Count != 2 || pc.Tactics[0] != "dip" || pc.Tactics[1] != "barrier" || pc.BarrierX != 50 || pc.StandBarrier != 90) return "start course parse (chips)";

            // ---- the same order: the minimal difference
            d = AswOrderPanel.DraftFrom(gc, "course", null); d.SetCombo("barrier");
            c = AswOrderPanel.Commands(gc, d, out err);
            if (err != null || c.Count != 1 || c[0].Name != "asw_set_tactics") return "tactic only -> asw_set_tactics";
            var pt = AswCommandsParse.ParseSetTactics(c[0].Args);
            if (pt.Err != null || pt.Group != "ASW-1" || pt.Tactics.Count != 1 || pt.Tactics[0] != "barrier") return "set tactics parse";
            d = AswOrderPanel.DraftFrom(gc, "course", null); d.Depth = "above";
            c = AswOrderPanel.Commands(gc, d, out err);
            if (err != null || c.Count != 1 || c[0].Name != "asw_depth") return "depth only -> asw_depth";
            var pd = AswCommandsParse.ParseDepth(c[0].Args);
            if (pd.Err != null || pd.Group != "ASW-1" || pd.Mode != "above") return "depth parse";
            d.SetCombo("dip");
            c = AswOrderPanel.Commands(gc, d, out err);
            if (err != null || c.Count != 2 || c[0].Name != "asw_set_tactics" || c[1].Name != "asw_depth") return "tactic + depth -> both";
            d = AswOrderPanel.DraftFrom(gc, "course", null);
            c = AswOrderPanel.Commands(gc, d, out err);
            if (err != null || c.Count != 0) return "nothing changed -> no commands";
            // the chips without a lit chip = auto: the tactics of the group are set to the default of the controller
            d = AswOrderPanel.DraftFrom(gc, "course", null); d.SetCombo("auto");
            c = AswOrderPanel.Commands(gc, d, out err);
            if (err != null || c.Count != 1 || c[0].Name != "asw_set_tactics") return "auto -> asw_set_tactics";

            // ---- geometry edits -> the full order again
            d = AswOrderPanel.DraftFrom(gc, "course", null); d.GeoDirty = true; d.Course.CorridorXNm = 50; d.Course.CorridorLNm = 60;
            c = AswOrderPanel.Commands(gc, d, out err);
            if (err != null || c.Count != 1 || c[0].Name != "asw_search_course") return "geometry -> full course order: " + err;
            pc = AswCommandsParse.ParseCourse(c[0].Args);
            if (pc.Err != null || pc.CorridorX != 50 || pc.StandCorridor != 60 || pc.Depth != "below" || pc.Tactics.Count != 2 || pc.Tactics[0] != "dip" || pc.Tactics[1] != "corridor") return "full course parse";

            // ---- replacing another order, SAR included
            var gsar = Grp("SAR-1", "sar", "alternate", "dip");
            d = AswOrderPanel.DraftFrom(gsar, "course", null);
            if (AswOrderPanel.Hint(gsar, d) != "replace") return "course tab of a SAR group says replace";
            c = AswOrderPanel.Commands(gsar, d, out err);
            if (err != null || c.Count != 1 || c[0].Name != "asw_search_course") return "replace SAR with a course order";

            // ---- area mode
            var gaN = Grp("ASW-5", "none", null);
            d = AswOrderPanel.DraftFrom(gaN, "area", null);
            AswOrderPanel.Commands(gaN, d, out err);
            if (err != "need_area" || AswOrderPanel.Hint(gaN, d) != "need_area") return "area without an area: " + err;
            var act = AswOrderPanel.Action(gaN, d);
            if (act.Enabled || act.Id != "start_area" || act.Tip != T("asw.hint.need_area")) return "start area is grey without an area";
            d.AreaSource = "rect"; d.Rect = new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 }; d.AreaPattern = "grid";
            act = AswOrderPanel.Action(gaN, d);
            if (!act.Enabled || act.Id != "start_area" || act.Label != T("asw.btn.start_area")) return "start area with a rect";
            c = AswOrderPanel.Commands(gaN, d, out err);
            if (err != null || c.Count != 1 || c[0].Name != "asw_search_area") return "area rect: " + err;
            var pa = AswCommandsParse.ParseArea(c[0].Args);
            if (pa.Err != null || pa.Rect == null || pa.Rect.Length != 8 || pa.Tactics.Count != 1 || pa.Tactics[0] != "grid" || Str(c[0].Args, "group") != "ASW-5") return "area rect parse";
            d.AreaSource = "bearing"; d.BearingDeg = 45; d.DistNm = 30; d.SizeNm = 20; d.Rect = null; d.AreaPattern = "auto";
            c = AswOrderPanel.Commands(gaN, d, out err);
            pa = c.Count == 1 ? AswCommandsParse.ParseArea(c[0].Args) : null;
            if (pa == null || pa.Err != null || pa.Bearing != 45 || pa.DistNm != 30 || pa.SizeNm != 20 || pa.Tactics.Count != 0) return "area bearing parse";
            // an area group: the pattern alone does not ask for the area again
            var ga = Grp("ASW-6", "area", "alternate", "grid");
            ga.Rect = new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 };
            d = AswOrderPanel.DraftFrom(ga, "area", null); d.AreaPattern = "dip";
            c = AswOrderPanel.Commands(ga, d, out err);
            if (err != null || c.Count != 1 || c[0].Name != "asw_set_tactics") return "area pattern only";
            // a tactic the composition cannot fly
            var gnm = new AswGroupView { Name = "ASW-7", Order = "area", Depth = "alternate", AreaPattern = "grid" };
            gnm.Tactics.Add("grid"); gnm.MemberRows.Add(Row(true, true, false)); gnm.Rect = ga.Rect;
            d = AswOrderPanel.DraftFrom(gnm, "area", null); d.AreaPattern = "mad";
            AswOrderPanel.Commands(gnm, d, out err);
            act = AswOrderPanel.Action(gnm, d);
            if (err != "tactic_unavailable" || act.Enabled || act.Tip != T("asw.tactic.no_mad")) return "tactic unavailable: " + err + " / " + act.Tip;
            // a course chip without a sensor is grey with the reason too; a draft never starts with a grey chip lit
            var gnb = new AswGroupView { Name = "ASW-10", Order = "none" };
            gnb.MemberRows.Add(Row(false, true, false));
            var dg = AswOrderPanel.DraftFrom(gnb, "course", null, new AswPatternParams { CourseTactic = "corridor" });
            if (dg.Combo != "auto") return "a grey chip is lit at the start: " + dg.Combo;
            var opts = AswWindowModel.TacticsFor("course", gnb);
            if (opts.Count != 3 || !opts[0].Enabled || opts[1].Enabled || opts[2].Enabled || opts[1].DisabledReason != T("asw.tactic.no_buoys")) return "course chips by the composition";
            dg.On[1] = true;
            AswOrderPanel.Commands(gnb, dg, out err);
            if (err != "tactic_unavailable") return "a grey chip refuses the order: " + err;

            // ---- Action of an existing order
            d = AswOrderPanel.DraftFrom(gc, "course", null);
            act = AswOrderPanel.Action(gc, d);
            if (act.Enabled || act.Id != "apply" || act.Label != T("asw.btn.apply") || act.Tip != T("asw.hint.same") || AswOrderPanel.Hint(gc, d) != null) return "apply is grey when the draft equals the order";
            d.Depth = "above";
            act = AswOrderPanel.Action(gc, d);
            if (!act.Enabled || AswOrderPanel.Hint(gc, d) != "dirty" || act.Tip == null || !act.Tip.Contains(T("asw.fld.depth"))) return "apply after a change";
            act = AswOrderPanel.Action(gNone, AswOrderPanel.DraftFrom(gNone, "course", null));
            if (!act.Enabled || act.Id != "start_course" || act.Label != T("asw.btn.start_course")) return "start course";
            act = AswOrderPanel.Action(null, AswOrderPanel.DraftFrom(null, "course", null));
            if (!act.Enabled || act.Id != "start_course") return "start course without a group";

            // ---- preview: the draft like the order, kept while the window holds it
            d = AswOrderPanel.DraftFrom(gc, "course", null); d.SetCombo("barrier"); d.Course.BarrierXNm = 30; d.Course.BarrierLNm = 60;
            var pv = AswOrderPanel.PreviewCommand(gc, d);
            pc = pv == null ? null : AswCommandsParse.ParseCourse(pv);
            if (pc == null || pc.Err != null || pc.Tactics[0] != "barrier" || pc.BarrierX != 30 || pc.StandBarrier != 60 || Str(pv, "group") != "ASW-1" || !(pv["keep"] is bool) || !(bool)pv["keep"]) return "preview";
            if (AswOrderPanel.PreviewCommand(gc, AswOrderPanel.DraftFrom(gc, "area", null)) != null) return "no preview of an area without an area";
            var pr = AswOrderPanel.DraftFrom(gNone, "area", null); pr.AreaSource = "bearing";
            var pva = AswOrderPanel.PreviewCommand(gNone, pr);
            if (pva == null || !(pva["area"] is bool) || AswCommandsParse.ParseArea(pva).Err != null) return "area preview by bearing";

            // ---- Stop
            var s = AswOrderPanel.Stop(null);
            if (s.Enabled || s.Id != "stop" || s.Tip != T("asw.hint.no_group")) return "stop: no group";
            s = AswOrderPanel.Stop(gNone);
            if (s.Enabled || s.Tip != T("asw.hint.no_order")) return "stop: no order";
            s = AswOrderPanel.Stop(gsar);
            if (s.Enabled) return "stop: a SAR group has its own stop";
            s = AswOrderPanel.Stop(gc);
            if (!s.Enabled || !s.Label.Contains("ASW-1") || s.Tip != null) return "stop: course group";
            s = AswOrderPanel.Stop(ga);
            if (!s.Enabled) return "stop: area group";
            return null;
        }
    }
}
